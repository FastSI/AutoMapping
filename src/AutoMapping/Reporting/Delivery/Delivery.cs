using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AutoMapping.Reporting;

/// <summary>A delivery failed. <see cref="IsTransient"/> tells the caller whether trying again can help.</summary>
public class ReportDeliveryException : Exception
{
    public ReportDeliveryException(string sink, string message, bool isTransient, Exception? inner = null) : base(message, inner)
    {
        Sink = sink;
        IsTransient = isTransient;
    }

    public string Sink { get; }
    public bool IsTransient { get; }
}

/// <summary>Raised at startup when the reporting configuration is unusable (fail fast, not at 3 a.m.).</summary>
public sealed class ReportingConfigurationException : Exception
{
    public ReportingConfigurationException(string message) : base(message) { }
}

public sealed record ReportDeliveryContext(string Application, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, string ScheduleName);

/// <summary>A destination for finished report files. Implementations must be safe to call from one thread at a time, must honour the
/// cancellation token, and must not put credentials into exception messages (they are logged and written to the outbox).</summary>
public interface IReportSink
{
    string Name { get; }
    Task DeliverAsync(ReportDeliveryContext context, IReadOnlyList<ReportFile> files, CancellationToken cancellationToken);
}

public sealed class RetryPolicy
{
    public int MaxAttempts { get; set; } = 4;
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Random spread (0..1) added to every delay so many instances do not retry in lockstep.</summary>
    public double Jitter { get; set; } = 0.2;
    /// <summary>Replaceable in tests.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (d, ct) => Task.Delay(d, ct);

    public TimeSpan DelayFor(int attempt)
    {
        var exponential = TimeSpan.FromTicks((long)Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * Math.Pow(2, Math.Max(0, attempt - 1))));
        var jitter = 1 + (Random.Shared.NextDouble() * 2 - 1) * Jitter;
        return TimeSpan.FromTicks((long)(exponential.Ticks * jitter));
    }

    /// <summary>Network and I/O faults and sink-flagged transient errors are retried; configuration and authentication errors are not.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        ReportDeliveryException d => d.IsTransient,
        TimeoutException or IOException or SocketException or HttpRequestException => true,
        OperationCanceledException => true,
        AggregateException a => a.InnerExceptions.All(IsTransient),
        _ => false
    };
}

public sealed record SinkResult(string Sink, bool Succeeded, int Attempts, string? Error, bool Transient);

/// <summary>Delivers files to sinks with retry, per-attempt timeout and failure isolation: one broken sink never blocks the others.</summary>
public sealed class ReportDeliveryService
{
    private readonly RetryPolicy _retry;
    private readonly ILogger _logger;

    public ReportDeliveryService(RetryPolicy retry, ILogger logger)
    {
        _retry = retry;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SinkResult>> DeliverAsync(IEnumerable<IReportSink> sinks, ReportDeliveryContext context,
        IReadOnlyList<ReportFile> files, CancellationToken cancellationToken)
    {
        var results = new List<SinkResult>();
        foreach (var sink in sinks)
            results.Add(await DeliverToAsync(sink, context, files, _retry.MaxAttempts, cancellationToken).ConfigureAwait(false));
        return results;
    }

    public async Task<SinkResult> DeliverToAsync(IReportSink sink, ReportDeliveryContext context, IReadOnlyList<ReportFile> files,
        int maxAttempts, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_retry.AttemptTimeout);
            try
            {
                await sink.DeliverAsync(context, files, timeout.Token).ConfigureAwait(false);
                if (attempt > 1) _logger.LogInformation("Report delivery to {Sink} succeeded on attempt {Attempt}.", sink.Name, attempt);
                return new SinkResult(sink.Name, true, attempt, null, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;   // shutting down: leave it to the outbox on the next start
            }
            catch (Exception ex)
            {
                last = ex is OperationCanceledException ? new TimeoutException($"Attempt timed out after {_retry.AttemptTimeout}.", ex) : ex;
                var transient = RetryPolicy.IsTransient(last);
                _logger.LogWarning("Report delivery to {Sink} failed on attempt {Attempt}/{Max} ({Kind}): {Error}",
                    sink.Name, attempt, maxAttempts, transient ? "transient" : "permanent", Describe(last));
                if (!transient || attempt >= maxAttempts) break;
                await _retry.Delay(_retry.DelayFor(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
        return new SinkResult(sink.Name, false, Math.Max(1, maxAttempts), Describe(last!), RetryPolicy.IsTransient(last!));
    }

    internal static string Describe(Exception ex)
    {
        var message = ex.Message.ReplaceLineEndings(" ");
        if (message.Length > 300) message = message[..300] + "…";
        return $"{ex.GetType().Name}: {message}";
    }
}

/// <summary>On-disk queue of deliveries that failed, retried later with backoff; entries that never succeed move to a dead-letter folder.</summary>
public sealed class ReportOutbox
{
    public sealed class Options
    {
        public string Directory { get; set; } = "";
        public int MaxEntries { get; set; } = 200;
        public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(7);
        public int MaxAttempts { get; set; } = 20;
        public TimeSpan DeadLetterRetention { get; set; } = TimeSpan.FromDays(30);
    }

    private sealed class Manifest
    {
        public string Sink { get; set; } = "";
        public DateTimeOffset Created { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        public int Attempts { get; set; }
        public string? LastError { get; set; }
        public ReportDeliveryContext Context { get; set; } = null!;
        public List<string> Files { get; set; } = new();
        public List<string> ContentTypes { get; set; } = new();
    }

    private readonly Options _options;
    private readonly ILogger _logger;

    public ReportOutbox(Options options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    private string Pending => Path.Combine(_options.Directory, "pending");
    private string Dead => Path.Combine(_options.Directory, "dead");

    public int PendingCount => System.IO.Directory.Exists(Pending) ? System.IO.Directory.GetDirectories(Pending).Length : 0;

    public void Enqueue(string sink, ReportDeliveryContext context, IReadOnlyList<ReportFile> files, string error, DateTimeOffset now)
    {
        System.IO.Directory.CreateDirectory(Pending);
        Restrict(_options.Directory);
        Restrict(Pending);
        EnforceCap();
        var id = $"{now.UtcDateTime:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}";
        var staging = Path.Combine(Pending, "." + id + ".tmp");
        System.IO.Directory.CreateDirectory(staging);
        Restrict(staging);
        var manifest = new Manifest { Sink = sink, Created = now, NextAttempt = now + TimeSpan.FromMinutes(5), Attempts = 1, LastError = error, Context = context };
        foreach (var file in files)
        {
            var name = SafeFileName(file.FileName);
            File.WriteAllBytes(Path.Combine(staging, name), file.Content);
            manifest.Files.Add(name);
            manifest.ContentTypes.Add(file.ContentType);
        }
        File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest));
        System.IO.Directory.Move(staging, Path.Combine(Pending, id));   // visible only when complete
        _logger.LogWarning("Report for {Sink} queued in the outbox ({Id}); it will be retried.", sink, id);
    }

    /// <summary>Retries every entry whose time has come. Returns (delivered, still pending, dead-lettered).</summary>
    public async Task<(int Delivered, int Pending, int Dead)> ProcessAsync(Func<string, IReportSink?> findSink, ReportDeliveryService delivery,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        int delivered = 0, pending = 0, dead = 0;
        if (!System.IO.Directory.Exists(Pending)) return (0, 0, 0);

        foreach (var directory in System.IO.Directory.GetDirectories(Pending).OrderBy(d => d, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.GetFileName(directory).StartsWith('.')) continue;   // being written
            Manifest? manifest;
            try { manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "manifest.json"))); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Outbox entry {Entry} is unreadable and was moved to the dead-letter folder.", Path.GetFileName(directory));
                MoveToDead(directory);
                dead++;
                continue;
            }
            // the manifest is read back from disk: never trust it (path traversal, mismatched lists, missing context)
            if (manifest == null || manifest.Context == null || manifest.Files.Count != manifest.ContentTypes.Count ||
                manifest.Files.Any(n => string.IsNullOrEmpty(n) || Path.GetFileName(n) != n || n.Contains("..")))
            {
                _logger.LogError("Outbox entry {Entry} has an invalid manifest and was moved to the dead-letter folder.", Path.GetFileName(directory));
                MoveToDead(directory);
                dead++;
                continue;
            }

            if (now - manifest.Created > _options.MaxAge || manifest.Attempts >= _options.MaxAttempts)
            {
                _logger.LogError("Giving up on a report for {Sink} after {Attempts} attempts; kept in the dead-letter folder.", manifest.Sink, manifest.Attempts);
                MoveToDead(directory);
                dead++;
                continue;
            }
            if (manifest.NextAttempt > now) { pending++; continue; }

            var sink = findSink(manifest.Sink);
            if (sink == null)
            {
                _logger.LogError("Outbox entry refers to unknown sink {Sink}; moved to the dead-letter folder.", manifest.Sink);
                MoveToDead(directory);
                dead++;
                continue;
            }

            List<ReportFile> files;
            try { files = manifest.Files.Select((name, i) => new ReportFile(name, manifest.ContentTypes[i], File.ReadAllBytes(Path.Combine(directory, name)))).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Outbox entry {Entry} has unreadable files and was moved to the dead-letter folder.", Path.GetFileName(directory));
                MoveToDead(directory);
                dead++;
                continue;
            }
            var result = await delivery.DeliverToAsync(sink, manifest.Context, files, 1, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                System.IO.Directory.Delete(directory, recursive: true);
                delivered++;
                continue;
            }

            manifest.Attempts++;
            manifest.LastError = result.Error;
            manifest.NextAttempt = now + TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(12).Ticks, TimeSpan.FromMinutes(5).Ticks * (long)Math.Pow(2, Math.Min(manifest.Attempts, 10))));
            if (!result.Transient) manifest.Attempts = Math.Max(manifest.Attempts, _options.MaxAttempts - 2);   // permanent errors get few more tries
            File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest));
            pending++;
        }

        CleanDeadLetters(now);
        return (delivered, pending, dead);
    }

    private void EnforceCap()
    {
        var entries = System.IO.Directory.GetDirectories(Pending).Where(d => !Path.GetFileName(d).StartsWith('.')).OrderBy(d => d, StringComparer.Ordinal).ToList();
        while (entries.Count >= _options.MaxEntries)
        {
            _logger.LogError("Outbox is full ({Max} entries); the oldest entry moved to the dead-letter folder.", _options.MaxEntries);
            MoveToDead(entries[0]);
            entries.RemoveAt(0);
        }
    }

    private void MoveToDead(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Dead);
            var target = Path.Combine(Dead, Path.GetFileName(directory));
            if (System.IO.Directory.Exists(target)) target += "-" + Guid.NewGuid().ToString("N")[..6];
            System.IO.Directory.Move(directory, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not move outbox entry {Entry} to the dead-letter folder.", Path.GetFileName(directory));
        }
    }

    private void CleanDeadLetters(DateTimeOffset now)
    {
        if (!System.IO.Directory.Exists(Dead)) return;
        foreach (var d in System.IO.Directory.GetDirectories(Dead))
        {
            try { if (now - System.IO.Directory.GetCreationTimeUtc(d) > _options.DeadLetterRetention) System.IO.Directory.Delete(d, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* try again next time */ }
        }
    }

    /// <summary>Reports can hold sensitive data: on Unix the outbox is owner-only (0700). On Windows the default per-user
    /// application-data folder is already private; if you set a shared StateDirectory, restrict its ACL yourself.</summary>
    private void Restrict(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Could not restrict the permissions of '{Directory}'.", directory);
        }
    }

    private static string SafeFileName(string name) => Path.GetFileName(name) == name && !name.Contains("..") ? name : throw new ArgumentException("Unsafe file name.", nameof(name));
}

/// <summary>Writes report files into a folder (a share, a mounted volume, a log directory). Atomic: readers never see half a file.</summary>
public sealed class LocalDirectorySink : IReportSink
{
    private static readonly System.Text.RegularExpressions.Regex OwnFile = new(
        @"^AutoMapping-.+-\d{8}T\d{6}Z(-performance|-exceptions)?\.(json|html|md|csv)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly string _directory;
    private readonly int? _retentionDays;

    public LocalDirectorySink(string directory, int? retentionDays = null, string name = "local")
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
        _directory = Path.GetFullPath(directory);
        _retentionDays = retentionDays;
        Name = name;
    }

    public string Name { get; }

    public Task DeliverAsync(ReportDeliveryContext context, IReadOnlyList<ReportFile> files, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(file.FileName) != file.FileName) throw new ReportDeliveryException(Name, "Unsafe report file name.", false);
                var target = Path.Combine(_directory, file.FileName);
                var temp = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(temp, file.Content);
                File.Move(temp, target, overwrite: true);
            }
            Cleanup();
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a full disk or a busy share can clear up; a missing permission cannot
            throw new ReportDeliveryException(Name, $"Could not write to '{_directory}': {ex.Message}", ex is IOException, ex);
        }
    }

    private void Cleanup()
    {
        if (_retentionDays is not > 0) return;
        var limit = DateTime.UtcNow.AddDays(-_retentionDays.Value);
        foreach (var path in Directory.EnumerateFiles(_directory, "AutoMapping-*"))
        {
            // only files this library produces (see ReportRenderer.BaseName): other files in a shared folder are never touched
            if (!OwnFile.IsMatch(Path.GetFileName(path))) continue;
            try { if (File.GetLastWriteTimeUtc(path) < limit) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not worth failing a delivery */ }
        }
    }
}

/// <summary>POSTs the JSON report (or the first file) to an HTTPS endpoint. With a secret, <c>X-AutoMapping-Signature</c> is
/// <c>sha256=</c> + hex HMAC-SHA256 over <c>"{X-AutoMapping-Timestamp}.{body}"</c> (timestamp in Unix seconds).</summary>
public sealed class WebhookSink : IReportSink
{
    private readonly Uri _url;
    private readonly HttpClient _client;
    private readonly byte[]? _secret;

    public WebhookSink(Uri url, string? signingSecret = null, HttpClient? client = null, bool allowInsecureHttp = false, string name = "webhook")
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && url.Scheme == Uri.UriSchemeHttp))
            throw new ReportingConfigurationException("The webhook URL must use https.");
        _url = url;
        // no redirects by default: a redirect would re-send the report and its signature to a host the caller never chose
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _secret = string.IsNullOrEmpty(signingSecret) ? null : Encoding.UTF8.GetBytes(signingSecret);
        Name = name;
    }

    public string Name { get; }

    public async Task DeliverAsync(ReportDeliveryContext context, IReadOnlyList<ReportFile> files, CancellationToken cancellationToken)
    {
        var file = files.FirstOrDefault(f => f.FileName.EndsWith(".json", StringComparison.Ordinal)) ?? files.FirstOrDefault()
                   ?? throw new ReportDeliveryException(Name, "There is nothing to send.", false);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url) { Content = new ByteArrayContent(file.Content) };
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(file.ContentType);
        request.Headers.Add("X-AutoMapping-Application", ReportRenderer.Safe(context.Application));
        request.Headers.Add("X-AutoMapping-Period", $"{context.PeriodStart.UtcDateTime:o}/{context.PeriodEnd.UtcDateTime:o}");
        if (_secret != null)
        {
            // the timestamp is signed with the body, so a captured request cannot be replayed later: receivers should reject old timestamps
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var payload = Encoding.UTF8.GetBytes(timestamp + ".").Concat(file.Content).ToArray();
            request.Headers.Add("X-AutoMapping-Timestamp", timestamp);
            request.Headers.Add("X-AutoMapping-Signature", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(_secret, payload)).ToLowerInvariant());
        }

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return;
            var code = (int)response.StatusCode;
            throw new ReportDeliveryException(Name, $"The endpoint answered HTTP {code}.", code >= 500 || code is 408 or 429);
        }
        catch (HttpRequestException ex)
        {
            throw new ReportDeliveryException(Name, $"The request failed: {ex.Message}", true, ex);
        }
    }

    public override string ToString() => $"{Name} ({_url.Host})";   // never the path or secret: they may contain tokens
}
