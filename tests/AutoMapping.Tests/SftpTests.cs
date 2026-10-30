using System.Text;
using AutoMapping.Reporting;
using AutoMapping.Reporting.Sftp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoMapping.Tests;

internal sealed class FakeSftpServer
{
    public HashSet<string> Directories { get; } = new() { "/" };
    public Dictionary<string, byte[]> Files { get; } = new();
    public List<string> Log { get; } = new();
    public Exception? FailOnUpload;
    public Exception? FailOnOpen;
    public int Opens;
}

internal sealed class FakeSftpFactory : ISftpSessionFactory
{
    private readonly FakeSftpServer _server;
    public FakeSftpFactory(FakeSftpServer server) => _server = server;

    public ISftpSession Open(SftpOptions options, CancellationToken cancellationToken)
    {
        _server.Opens++;
        if (_server.FailOnOpen != null) throw _server.FailOnOpen;
        return new Session(_server);
    }

    private sealed class Session : ISftpSession
    {
        private readonly FakeSftpServer _s;
        public Session(FakeSftpServer s) => _s = s;
        public bool DirectoryExists(string path) => _s.Directories.Contains(path);
        public void CreateDirectory(string path) { _s.Log.Add("mkdir " + path); _s.Directories.Add(path); }
        public bool FileExists(string path) => _s.Files.ContainsKey(path);
        public void Upload(Stream content, string path)
        {
            using var ms = new MemoryStream();
            content.CopyTo(ms);
            _s.Log.Add("upload " + path);
            if (_s.FailOnUpload != null) { _s.Files[path] = ms.ToArray().Take(1).ToArray(); throw _s.FailOnUpload; }   // leaves a partial file behind
            _s.Files[path] = ms.ToArray();
        }
        public void RenameReplacing(string from, string to) { _s.Log.Add($"rename {from} {to}"); _s.Files[to] = _s.Files[from]; _s.Files.Remove(from); }
        public void Delete(string path) { _s.Log.Add("delete " + path); _s.Files.Remove(path); }
        public void Dispose() { }
    }
}

[Collection("ProcessEnvironment")]
public class SftpTests
{
    private static SftpOptions Options(Action<SftpOptions>? tweak = null)
    {
        var o = new SftpOptions { Host = "sftp.example.com", Username = "reports", Password = "pw", RemoteDirectory = "/inbox/AutoMapping" };
        o.HostKeyFingerprints.Add("SHA256:abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG");
        tweak?.Invoke(o);
        return o;
    }

    private static ReportDeliveryContext Ctx() => new("app", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(1), "daily-0");
    private static IReadOnlyList<ReportFile> Files() => new[]
    {
        new ReportFile("AutoMapping-app-20260101T000000Z.json", "application/json", Encoding.UTF8.GetBytes("{\"ok\":true}")),
        new ReportFile("AutoMapping-app-20260101T000000Z.html", "text/html", Encoding.UTF8.GetBytes("<html/>"))
    };

    [Fact]
    public void Configuration_mistakes_fail_at_startup()
    {
        var factory = new FakeSftpFactory(new FakeSftpServer());
        Assert.Throws<ReportingConfigurationException>(() => new SftpReportSink(Options(o => o.HostKeyFingerprints.Clear()), factory));     // unpinned
        Assert.Throws<ReportingConfigurationException>(() => new SftpReportSink(Options(o => o.Host = ""), factory));
        Assert.Throws<ReportingConfigurationException>(() => new SftpReportSink(Options(o => o.Password = null), factory));                   // no credentials
        Assert.Throws<ReportingConfigurationException>(() => new SftpReportSink(Options(o => o.RemoteDirectory = "/a/../b"), factory));
        Assert.Throws<ReportingConfigurationException>(() => new SftpReportSink(Options(o => { o.HostKeyFingerprints.Clear(); o.HostKeyFingerprints.Add("MD5:aa:bb"); }), factory));
        new SftpReportSink(Options(o => { o.HostKeyFingerprints.Clear(); o.AcceptAnyHostKey = true; }), factory);                              // explicit opt-out is allowed
        new SftpReportSink(Options(o => { o.Password = null; o.PrivateKeyPem = "-----BEGIN OPENSSH PRIVATE KEY-----"; }), factory);           // key instead of password
    }

    [Fact]
    public void Files_are_uploaded_as_part_then_renamed_and_the_folder_is_created()
    {
        var server = new FakeSftpServer();
        var sink = new SftpReportSink(Options(), new FakeSftpFactory(server));
        sink.DeliverAsync(Ctx(), Files(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(server.Files["/inbox/AutoMapping/AutoMapping-app-20260101T000000Z.json"]));
        Assert.True(server.Files.Keys.All(k => !k.EndsWith(".part")));
        Assert.True(server.Directories.Contains("/inbox") && server.Directories.Contains("/inbox/AutoMapping"));
        var log = server.Log;
        Assert.True(log.IndexOf("upload /inbox/AutoMapping/AutoMapping-app-20260101T000000Z.json.part") < log.IndexOf("rename /inbox/AutoMapping/AutoMapping-app-20260101T000000Z.json.part /inbox/AutoMapping/AutoMapping-app-20260101T000000Z.json"));
    }

    [Fact]
    public void A_failed_upload_leaves_no_partial_file_and_is_classified()
    {
        var server = new FakeSftpServer { FailOnUpload = new IOException("connection reset") };
        var sink = new SftpReportSink(Options(), new FakeSftpFactory(server));
        var ex = Assert.Throws<ReportDeliveryException>(() => sink.DeliverAsync(Ctx(), Files(), CancellationToken.None).GetAwaiter().GetResult());
        Assert.True(ex.IsTransient);
        Assert.True(server.Files.Keys.All(k => !k.EndsWith(".part")));

        server.FailOnUpload = new UnauthorizedAccessException("permission denied");
        var denied = Assert.Throws<ReportDeliveryException>(() => sink.DeliverAsync(Ctx(), Files(), CancellationToken.None).GetAwaiter().GetResult());
        Assert.False(denied.IsTransient);
        Assert.True(server.Files.Keys.All(k => !k.EndsWith(".part")));
    }

    [Fact]
    public void Unsafe_file_names_never_reach_the_server()
    {
        var server = new FakeSftpServer();
        var sink = new SftpReportSink(Options(), new FakeSftpFactory(server));
        foreach (var name in new[] { "../x.json", "a/b.json", "a\\b.json", "..json" })
        {
            var files = new[] { new ReportFile(name, "application/json", new byte[] { 1 }) };
            Assert.Throws<ReportDeliveryException>(() => sink.DeliverAsync(Ctx(), files, CancellationToken.None).GetAwaiter().GetResult());
        }
        Assert.Equal(0, server.Opens);   // rejected before connecting
    }

    [Fact]
    public void Host_key_pinning_accepts_only_the_pinned_key()
    {
        var key = Encoding.ASCII.GetBytes("pretend this is a public host key blob");
        var fingerprint = SftpReportSink.Fingerprint(key);
        Assert.True(fingerprint.StartsWith("SHA256:") && !fingerprint.EndsWith("="));

        Assert.True(SftpReportSink.IsPinned(key, new[] { "SHA256:other", fingerprint }));
        Assert.True(SftpReportSink.IsPinned(key, new[] { fingerprint + "=" }));                       // padding tolerated
        Assert.False(SftpReportSink.IsPinned(key, new[] { "SHA256:other" }));
        Assert.False(SftpReportSink.IsPinned(Encoding.ASCII.GetBytes("a different key"), new[] { fingerprint }));
        Assert.False(SftpReportSink.IsPinned(key, Array.Empty<string>()));
    }

    [Fact]
    public void Sftp_failures_flow_through_retry_and_the_outbox()
    {
        var server = new FakeSftpServer { FailOnOpen = new ReportDeliveryException("sftp", "Could not connect", true) };
        var options = new ReportingOptions { Application = "x", StateDirectory = Path.Combine(Path.GetTempPath(), "AutoMapping-sftp-" + Guid.NewGuid().ToString("N")), Retry = { MaxAttempts = 2, Delay = (_, _) => Task.CompletedTask, Jitter = 0 } }
            .Daily(new TimeOnly(6, 0))
            .AddSftp(Options(), new FakeSftpFactory(server));
        var config = new MapperConfiguration(c => c.CreateMap<RpSrc, RpDst>());
        var time = new ManualTime { Now = new DateTimeOffset(2026, 10, 26, 6, 1, 0, TimeSpan.Zero) };
        var state = new InMemoryReportStateStore();
        state.SetLastRun("Daily-0", new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.Zero));
        var scheduler = new ReportScheduler(options, config, new MapperTelemetry(), new ProcessSampler(options.Sampler, () => default, time),
            new ReportingStatus(), new NoServices(), NullLogger<ReportScheduler>.Instance, time, state);
        try
        {
            scheduler.RunOnceAsync(time.Now, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(2, server.Opens);                              // two attempts, then queued
            Assert.Equal(1, scheduler.Status.OutboxPending);

            server.FailOnOpen = null;                                   // the server is back
            scheduler.RunOnceAsync(time.Now.AddMinutes(30), CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(0, scheduler.Status.OutboxPending);
            Assert.Contains(server.Files.Keys, k => k.EndsWith(".json"));
            Assert.True(scheduler.Status.Healthy);
        }
        finally
        {
            var dir = options.ResolveStateDirectoryForTests();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

internal static class ReportingOptionsTestExtensions
{
    public static string ResolveStateDirectoryForTests(this ReportingOptions o) => o.StateDirectory;
}
