using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace AutoMapping.Reporting.Sftp;

/// <summary>SSH.NET implementation of <see cref="ISftpSessionFactory"/>.</summary>
internal sealed class SshNetSessionFactory : ISftpSessionFactory
{
    public ISftpSession Open(SftpOptions options, CancellationToken cancellationToken)
    {
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(options.PrivateKeyPem) || !string.IsNullOrEmpty(options.PrivateKeyPath))
        {
            PrivateKeyFile key;
            try
            {
                if (!string.IsNullOrEmpty(options.PrivateKeyPem))
                {
                    using var pem = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(options.PrivateKeyPem));
                    key = string.IsNullOrEmpty(options.PrivateKeyPassphrase) ? new PrivateKeyFile(pem) : new PrivateKeyFile(pem, options.PrivateKeyPassphrase);
                }
                else key = string.IsNullOrEmpty(options.PrivateKeyPassphrase) ? new PrivateKeyFile(options.PrivateKeyPath!) : new PrivateKeyFile(options.PrivateKeyPath!, options.PrivateKeyPassphrase);
            }
            catch (Exception ex) when (ex is SshException or IOException or ArgumentException)
            {
                throw new ReportDeliveryException(options.Name, "The SFTP private key could not be read (wrong path, format or passphrase).", false, ex);
            }
            methods.Add(new PrivateKeyAuthenticationMethod(options.Username, key));
        }
        if (!string.IsNullOrEmpty(options.Password)) methods.Add(new PasswordAuthenticationMethod(options.Username, options.Password));

        var info = new ConnectionInfo(options.Host, options.Port, options.Username, methods.ToArray()) { Timeout = options.ConnectTimeout };
        var client = new SftpClient(info) { OperationTimeout = options.OperationTimeout };
        bool hostKeyRejected = false;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = options.AcceptAnyHostKey || SftpReportSink.IsPinned(e.HostKey, options.HostKeyFingerprints);
            hostKeyRejected |= !e.CanTrust;
        };

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (hostKeyRejected)   // never retried and never downgraded: this is either a misconfiguration or an attack
                throw new ReportDeliveryException(options.Name, "The SFTP server's host key does not match the pinned fingerprints. The report was NOT sent.", false, ex);
            if (ex is SshAuthenticationException)
                throw new ReportDeliveryException(options.Name, "SFTP authentication failed (check the username, password or key).", false, ex);
            if (ex is SshConnectionException or SocketException or SshOperationTimeoutException or TimeoutException or ProxyException)
                throw new ReportDeliveryException(options.Name, $"Could not connect to the SFTP server: {ex.GetType().Name}.", true, ex);
            throw new ReportDeliveryException(options.Name, $"Could not open the SFTP session: {ex.GetType().Name}.", false, ex);
        }
        return new Session(client);
    }

    private sealed class Session : ISftpSession
    {
        private readonly SftpClient _client;
        public Session(SftpClient client) => _client = client;

        public bool DirectoryExists(string path) => _client.Exists(path) && _client.GetAttributes(path).IsDirectory;
        public void CreateDirectory(string path) => _client.CreateDirectory(path);
        public bool FileExists(string path) => _client.Exists(path);
        public void Upload(Stream content, string path) => _client.UploadFile(content, path, canOverride: true);

        public void RenameReplacing(string from, string to)
        {
            // plain SFTP rename cannot overwrite, so an existing report of the same name is removed first. There is a brief window
            // in which the final name is absent; the .part upload means it is never half-written.
            if (_client.Exists(to)) _client.DeleteFile(to);
            _client.RenameFile(from, to);
        }

        public void Delete(string path)
        {
            if (_client.Exists(path)) _client.DeleteFile(path);
        }

        public void Dispose()
        {
            try { if (_client.IsConnected) _client.Disconnect(); } catch { /* closing */ }
            _client.Dispose();
        }
    }
}
