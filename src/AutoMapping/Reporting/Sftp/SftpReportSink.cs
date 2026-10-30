using System.Security.Cryptography;
using System.Text;

namespace AutoMapping.Reporting.Sftp;

public sealed class SftpOptions
{
    public string Name { get; set; } = "sftp";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";

    /// <summary>Password authentication. Prefer a key; load the secret from a vault or environment, never from source control.</summary>
    public string? Password { get; set; }
    /// <summary>OpenSSH/PEM private key text (alternative to <see cref="PrivateKeyPath"/>).</summary>
    public string? PrivateKeyPem { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }

    /// <summary>Remote folder reports are uploaded to (must exist or be creatable).</summary>
    public string RemoteDirectory { get; set; } = ".";

    /// <summary>
    /// Accepted host key fingerprints as printed by <c>ssh-keygen -lf</c> (<c>SHA256:...</c>). Required: without pinning, a
    /// man-in-the-middle could receive the reports. Set <see cref="AcceptAnyHostKey"/> only for throw-away test servers: it is refused when DOTNET_ENVIRONMENT or ASPNETCORE_ENVIRONMENT is "Production".
    /// </summary>
    public List<string> HostKeyFingerprints { get; } = new();
    public bool AcceptAnyHostKey { get; set; }

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(2);
    public bool CreateRemoteDirectory { get; set; } = true;

    private static bool IsProduction() =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Production", StringComparison.OrdinalIgnoreCase);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ReportingConfigurationException("SFTP: Host is required.");
        if (Port is < 1 or > 65535) throw new ReportingConfigurationException("SFTP: Port must be 1 to 65535.");
        if (string.IsNullOrWhiteSpace(Username)) throw new ReportingConfigurationException("SFTP: Username is required.");
        if (string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(PrivateKeyPem) && string.IsNullOrEmpty(PrivateKeyPath))
            throw new ReportingConfigurationException("SFTP: provide a Password, a PrivateKeyPem or a PrivateKeyPath.");
        if (HostKeyFingerprints.Count == 0 && !AcceptAnyHostKey)
            throw new ReportingConfigurationException("SFTP: pin the server with HostKeyFingerprints.Add(\"SHA256:...\") (see ssh-keygen -lf), or set AcceptAnyHostKey for a test server.");
        if (AcceptAnyHostKey && IsProduction())
            throw new ReportingConfigurationException("SFTP: AcceptAnyHostKey is refused when the environment is Production. Pin the server with HostKeyFingerprints.");
        foreach (var fp in HostKeyFingerprints)
            if (!fp.StartsWith("SHA256:", StringComparison.Ordinal)) throw new ReportingConfigurationException("SFTP: fingerprints must look like \"SHA256:base64...\".");
        if (RemoteDirectory.Split('/', '\\').Contains("..")) throw new ReportingConfigurationException("SFTP: RemoteDirectory must not contain '..'.");
    }
}

/// <summary>The few remote operations the sink needs. The SSH.NET adapter implements it; tests use a fake.</summary>
public interface ISftpSession : IDisposable
{
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    bool FileExists(string path);
    void Upload(Stream content, string path);
    /// <summary>Moves <paramref name="from"/> to <paramref name="to"/>, replacing an existing file (not atomic on servers that cannot overwrite on rename).</summary>
    void RenameReplacing(string from, string to);
    void Delete(string path);
}

public interface ISftpSessionFactory
{
    /// <summary>Connects and authenticates. Must throw <see cref="ReportDeliveryException"/> with the right <c>IsTransient</c>.</summary>
    ISftpSession Open(SftpOptions options, CancellationToken cancellationToken);
}

/// <summary>Uploads each file as <c>name.part</c> and renames it when complete, so a consumer polling the folder never reads a half-written report.</summary>
public sealed class SftpReportSink : IReportSink
{
    private readonly SftpOptions _options;
    private readonly ISftpSessionFactory _factory;

    public SftpReportSink(SftpOptions options, ISftpSessionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public string Name => _options.Name;

    public Task DeliverAsync(ReportDeliveryContext context, IReadOnlyList<ReportFile> files, CancellationToken cancellationToken)
    {
        // SSH.NET is synchronous; run on the pool so the scheduler thread is never blocked on the network
        return Task.Run(() => Deliver(files, cancellationToken), cancellationToken);
    }

    private void Deliver(IReadOnlyList<ReportFile> files, CancellationToken ct)
    {
        foreach (var file in files)
            if (Path.GetFileName(file.FileName) != file.FileName || file.FileName.Contains("..") || file.FileName.Contains('/') || file.FileName.Contains('\\'))
                throw new ReportDeliveryException(Name, "Unsafe report file name.", false);

        using var session = _factory.Open(_options, ct);
        var directory = _options.RemoteDirectory.TrimEnd('/');
        if (directory.Length == 0) directory = "/";

        try
        {
            if (_options.CreateRemoteDirectory) EnsureDirectory(session, directory);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var final = Combine(directory, file.FileName);
                var part = final + ".part";
                try
                {
                    using (var stream = new MemoryStream(file.Content, writable: false)) session.Upload(stream, part);
                    session.RenameReplacing(part, final);
                }
                catch
                {
                    try { session.Delete(part); } catch { /* best effort: do not hide the original failure */ }
                    throw;
                }
            }
        }
        catch (ReportDeliveryException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new ReportDeliveryException(Name, $"The SFTP transfer failed: {ex.GetType().Name}: {ex.Message}", true, ex);
        }
        catch (Exception ex)
        {
            // unknown server-side errors (permission denied, quota, bad path): do not hammer the server, let the outbox slow-retry
            throw new ReportDeliveryException(Name, $"The SFTP server rejected the upload: {ex.GetType().Name}: {ex.Message}", false, ex);
        }
    }

    private static string Combine(string directory, string name) => directory == "/" ? "/" + name : directory + "/" + name;

    private static void EnsureDirectory(ISftpSession session, string path)
    {
        if (path is "." or "/" or "" || session.DirectoryExists(path)) return;
        var built = path.StartsWith('/') ? "/" : "";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            built = built.Length == 0 || built == "/" ? built + part : built + "/" + part;
            if (!session.DirectoryExists(built)) session.CreateDirectory(built);
        }
    }

    /// <summary>OpenSSH style fingerprint (<c>SHA256:</c> + unpadded base64) of a raw public host key.</summary>
    public static string Fingerprint(byte[] hostKey) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    /// <summary>True when the presented key matches one of the pinned fingerprints (compared in constant time).</summary>
    public static bool IsPinned(byte[] hostKey, IEnumerable<string> pinned)
    {
        var actual = Encoding.ASCII.GetBytes(Fingerprint(hostKey));
        var match = false;
        foreach (var fp in pinned)
        {
            var wanted = Encoding.ASCII.GetBytes(fp.TrimEnd('='));
            match |= wanted.Length == actual.Length && CryptographicOperations.FixedTimeEquals(wanted, actual);
        }
        return match;
    }
}

public static class SftpReportingExtensions
{
    /// <summary>Adds an SFTP destination: <c>options.AddSftp(o => { o.Host = ...; o.HostKeyFingerprints.Add("SHA256:..."); })</c>.</summary>
    public static ReportingOptions AddSftp(this ReportingOptions options, Action<SftpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var sftp = new SftpOptions();
        configure(sftp);
        sftp.Validate();   // misconfiguration stops the application at startup
        return options.AddSink(new SftpReportSink(sftp, new SshNetSessionFactory()));
    }

    /// <summary>For tests and custom transports.</summary>
    public static ReportingOptions AddSftp(this ReportingOptions options, SftpOptions sftp, ISftpSessionFactory factory) =>
        options.AddSink(new SftpReportSink(sftp, factory));
}
