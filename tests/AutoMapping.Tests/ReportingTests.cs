using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoMapping;
using AutoMapping.Reporting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoMapping.Tests;

public class RpSrc { public int Id { get; set; } public string Name { get; set; } = ""; }
public class RpDst { public int Id { get; set; } public string Name { get; set; } = ""; }

internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class NoServices : IServiceProvider { public object? GetService(Type serviceType) => null; }

internal sealed class FakeSink : IReportSink
{
    public FakeSink(string name) => Name = name;
    public string Name { get; }
    public List<IReadOnlyList<ReportFile>> Delivered { get; } = new();
    public int Calls;
    public Queue<Exception> Failures { get; } = new();
    public Task DeliverAsync(ReportDeliveryContext context, IReadOnlyList<ReportFile> files, CancellationToken ct)
    {
        Calls++;
        if (Failures.Count > 0) throw Failures.Dequeue();
        Delivered.Add(files);
        return Task.CompletedTask;
    }
}

internal sealed class StubHandler : HttpMessageHandler
{
    public HttpStatusCode Status = HttpStatusCode.OK;
    public HttpRequestMessage? Last;
    public byte[]? Body;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Last = request;
        Body = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(ct);
        return new HttpResponseMessage(Status);
    }
}

public class ReportingTests
{
    private static MapperConfiguration NewConfig() => new(c => c.CreateMap<RpSrc, RpDst>());
    private static void Run(Task task) => task.GetAwaiter().GetResult();
    private static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "AutoMapping-tests-" + Guid.NewGuid().ToString("N"));

    private static RetryPolicy FastRetry(int attempts = 3) => new() { MaxAttempts = attempts, Delay = (_, _) => Task.CompletedTask, Jitter = 0 };

    // ------------------------------------------------------------------ observer and telemetry

    [Fact]
    public void Observer_sees_calls_failures_and_compiles_and_never_breaks_mapping()
    {
        var config = new MapperConfiguration(c =>
        {
            c.CreateMap<RpSrc, RpDst>().ForMember(d => d.Name, o => o.MapFrom(new Func<RpSrc, RpDst, string>((s, d) => s.Id < 0 ? throw new InvalidOperationException("bad") : s.Name)));
        });
        var telemetry = new MapperTelemetry();
        config.Observer = telemetry;
        var mapper = config.CreateMapper();

        for (var i = 0; i < 5; i++) mapper.Map<RpDst>(new RpSrc { Id = i, Name = "n" });
        Assert.Throws<InvalidOperationException>(() => mapper.Map<RpDst>(new RpSrc { Id = -1 }));
        Assert.Throws<InvalidOperationException>(() => mapper.Map<RpSrc, RpDst>(new RpSrc { Id = -1 }));

        var snapshot = telemetry.Snapshot();
        var pair = Assert.Single(snapshot.Pairs);
        Assert.Equal(7, pair.Calls);
        Assert.Equal(2, pair.Errors);
        Assert.True(snapshot.Compiles.Count >= 1);
        Assert.Equal(2, Assert.Single(snapshot.Exceptions).Count);   // both failures come from the same place: one group

        config.Observer = new ThrowingObserver();     // a broken observer must not change mapping results
        Assert.Equal("n", mapper.Map<RpDst>(new RpSrc { Id = 1, Name = "n" }).Name);
    }

    private sealed class ThrowingObserver : IMapperObserver
    {
        public void OnMapped(Type s, Type d, long t) => throw new Exception("boom");
        public void OnFailed(Type s, Type d, long t, Exception e) => throw new Exception("boom");
        public void OnCompiled(Type s, Type d, TimeSpan t, bool l) => throw new Exception("boom");
    }

    [Fact]
    public void Telemetry_memory_is_bounded()
    {
        var telemetry = new MapperTelemetry(new TelemetryOptions { MaxTrackedPairs = 3, MaxExceptionGroups = 2 });
        var types = new[] { typeof(int), typeof(string), typeof(long), typeof(double), typeof(byte), typeof(char), typeof(bool), typeof(decimal) };
        foreach (var t in types) telemetry.OnMapped(t, typeof(object), 1000);
        for (var i = 0; i < 5; i++) telemetry.OnFailed(typeof(int), typeof(string), 10, new Exception("x" + i));   // same site -> one group per type+frames
        foreach (var t in types.Take(4)) telemetry.OnFailed(t, typeof(object), 10, new ArgumentException("y"));

        var snapshot = telemetry.Snapshot();
        Assert.True(snapshot.Pairs.Count <= 4);                  // 3 tracked + "(other)"
        Assert.Contains(snapshot.Pairs, p => p.Source == "(other)");
        Assert.True(snapshot.Exceptions.Count <= 2);
        Assert.True(snapshot.DroppedExceptionGroups > 0);
    }

    [Fact]
    public void Exception_messages_are_hidden_unless_they_are_ours_or_enabled()
    {
        var hidden = new MapperTelemetry();
        hidden.OnFailed(typeof(int), typeof(string), 1, new InvalidOperationException("customer ssn 123-45-6789"));
        hidden.OnFailed(typeof(int), typeof(long), 1, new MappingException("Maximum mapping depth exceeded"));
        var snapshot = hidden.Snapshot();
        Assert.Null(snapshot.Exceptions.Single(e => e.ExceptionType.Contains("InvalidOperation")).Message);
        Assert.NotNull(snapshot.Exceptions.Single(e => e.ExceptionType.Contains("MappingException")).Message);

        var shown = new MapperTelemetry(new TelemetryOptions { CaptureExceptionMessages = true });
        shown.OnFailed(typeof(int), typeof(string), 1, new InvalidOperationException("visible"));
        Assert.Equal("visible", shown.Snapshot().Exceptions[0].Message);
    }

    [Fact]
    public void Histogram_percentiles_are_upper_bounds()
    {
        var h = new LatencyHistogram();
        for (var i = 0; i < 99; i++) h.Record(10);     // bucket [8,16)
        h.Record(5000);                                // bucket [4096,8192)
        var counts = h.Snapshot();
        Assert.Equal(16.0, LatencyHistogram.Percentile(counts, 0.50));
        Assert.Equal(16.0, LatencyHistogram.Percentile(counts, 0.95));
        Assert.Equal(8192.0, LatencyHistogram.Percentile(counts, 1.0));
        Assert.Equal(0.0, LatencyHistogram.Percentile(new long[32], 0.5));
    }

    // ------------------------------------------------------------------ leak analysis

    private static List<LiveSetObservation> Series(Func<int, long> bytes, int count = 12, int minutesApart = 30)
    {
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        return Enumerable.Range(0, count).Select(i => new LiveSetObservation(start.AddMinutes(i * minutesApart), bytes(i))).ToList();
    }

    [Fact]
    public void Leak_analyzer_flags_steady_growth_and_ignores_noise_and_short_series()
    {
        var growing = LeakAnalyzer.Analyze(Series(i => 100_000_000 + i * 20_000_000L));
        Assert.Equal(LeakVerdict.Suspected, growing.Verdict);
        Assert.True(growing.SlopeBytesPerHour > 1024 * 1024);

        var rng = new Random(1);
        var flat = LeakAnalyzer.Analyze(Series(i => 100_000_000 + rng.Next(-5_000_000, 5_000_000)));
        Assert.Equal(LeakVerdict.Stable, flat.Verdict);

        var sawtooth = LeakAnalyzer.Analyze(Series(i => 100_000_000 + (i % 4) * 30_000_000L));
        Assert.Equal(LeakVerdict.Stable, sawtooth.Verdict);

        Assert.Equal(LeakVerdict.InsufficientData, LeakAnalyzer.Analyze(Series(i => 1, count: 3)).Verdict);
        Assert.Equal(LeakVerdict.InsufficientData, LeakAnalyzer.Analyze(Series(i => 1, count: 8, minutesApart: 1)).Verdict);
    }

    // ------------------------------------------------------------------ schedules

    private static DateTimeOffset Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void Daily_weekly_monthly_schedules_find_next_and_previous_runs()
    {
        var daily = ReportSchedule.Daily(new TimeOnly(6, 0));
        Assert.Equal(Utc(2026, 10, 27, 6), daily.NextAfter(Utc(2026, 10, 26, 6, 0)));
        Assert.Equal(Utc(2026, 10, 26, 6), daily.NextAfter(Utc(2026, 10, 26, 5, 59)));
        Assert.Equal(Utc(2026, 10, 25, 6), daily.PreviousBefore(Utc(2026, 10, 26, 6, 0)));

        var weekly = ReportSchedule.Weekly(DayOfWeek.Monday, new TimeOnly(8, 30));
        Assert.Equal(Utc(2026, 11, 2, 8, 30), weekly.NextAfter(Utc(2026, 10, 26, 9, 0)));   // 26 Oct 2026 is a Monday
        Assert.Equal(Utc(2026, 10, 19, 8, 30), weekly.PreviousBefore(Utc(2026, 10, 26, 8, 30)));

        var monthly = ReportSchedule.Monthly(31, new TimeOnly(0, 0));
        Assert.Equal(Utc(2026, 2, 28), monthly.NextAfter(Utc(2026, 1, 31, 12)));            // clamps to the last day
        Assert.Equal(Utc(2026, 3, 31), monthly.NextAfter(Utc(2026, 2, 28, 0)));
        Assert.Equal(Utc(2028, 2, 29), monthly.NextAfter(Utc(2028, 1, 31, 12)));            // leap year
        Assert.Throws<ArgumentOutOfRangeException>(() => ReportSchedule.Monthly(32, new TimeOnly(0, 0)));
    }

    [Fact]
    public void Schedules_respect_time_zones_and_dst_gaps()
    {
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var daily = ReportSchedule.Daily(new TimeOnly(9, 0), india);                       // 09:00 IST = 03:30 UTC
        Assert.Equal(Utc(2026, 10, 27, 3, 30), daily.NextAfter(Utc(2026, 10, 26, 12, 0)));

        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var gap = ReportSchedule.Daily(new TimeOnly(2, 30), newYork);                       // 2026-03-08 02:30 does not exist
        var run = gap.NextAfter(Utc(2026, 3, 8, 0, 0));
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), run);
    }

    // ------------------------------------------------------------------ report building and rendering

    private static DiagnosticsReport SampleReport(string? evilText = null)
    {
        var telemetry = new MapperTelemetry(new TelemetryOptions { CaptureExceptionMessages = true });
        for (var i = 0; i < 200; i++) telemetry.OnMapped(typeof(RpSrc), typeof(RpDst), 20_000);
        for (var i = 0; i < 30; i++) telemetry.OnFailed(typeof(RpSrc), typeof(RpDst), 20_000, new InvalidOperationException(evilText ?? "oops"));
        var builder = new ReportBuilder(evilText ?? "orders-api");
        var start = Utc(2026, 10, 25);
        return builder.Build(start, start.AddDays(1), telemetry.Snapshot(), null, Array.Empty<ProcessSample>(), Array.Empty<LiveSetObservation>(), start.AddDays(1));
    }

    [Fact]
    public void Report_contains_summary_exceptions_and_findings()
    {
        var report = SampleReport();
        Assert.Equal(230, report.Summary.TotalCalls);
        Assert.Equal(30, report.Summary.TotalErrors);
        Assert.Contains(report.Findings, f => f.Code == "MS-ERR-001" && f.Severity == FindingSeverity.Critical);   // 13 % errors
        Assert.Contains(report.Findings, f => f.Code == "MS-ERR-003");
        Assert.Equal(FindingSeverity.Critical, report.WorstSeverity);
        Assert.Equal(30, Assert.Single(report.Exceptions).Count);
    }

    [Fact]
    public void Report_is_the_difference_between_two_snapshots()
    {
        var telemetry = new MapperTelemetry();
        for (var i = 0; i < 100; i++) telemetry.OnMapped(typeof(RpSrc), typeof(RpDst), 1000);
        var first = telemetry.Snapshot();
        for (var i = 0; i < 40; i++) telemetry.OnMapped(typeof(RpSrc), typeof(RpDst), 1000);
        var report = new ReportBuilder("x").Build(Utc(2026, 10, 1), Utc(2026, 10, 2), telemetry.Snapshot(), first, Array.Empty<ProcessSample>(), Array.Empty<LiveSetObservation>(), Utc(2026, 10, 2));
        Assert.Equal(40, report.Summary.TotalCalls);
    }

    [Fact]
    public void Renderers_produce_valid_output_and_neutralise_hostile_text()
    {
        var evil = "<script>alert(1)</script>";
        var report = SampleReport(evil);
        var files = ReportRenderer.Render(report, new[] { ReportFormat.Json, ReportFormat.Html, ReportFormat.Markdown, ReportFormat.Csv });
        Assert.Equal(5, files.Count);

        using (var json = JsonDocument.Parse(files.Single(f => f.FileName.EndsWith(".json")).Content))
            Assert.Equal(230, json.RootElement.GetProperty("summary").GetProperty("totalCalls").GetInt64());

        var html = Encoding.UTF8.GetString(files.Single(f => f.FileName.EndsWith(".html")).Content);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Content-Security-Policy", html);

        Assert.True(files.All(f => Path.GetFileName(f.FileName) == f.FileName));   // application name cannot escape the folder
        Assert.False(files[0].FileName.Contains('<'));

        var formula = SampleReportWithFormula();
        var csv = Encoding.UTF8.GetString(ReportRenderer.Render(formula, new[] { ReportFormat.Csv }).Single(f => f.FileName.EndsWith("exceptions.csv")).Content);
        Assert.Contains("'=HYPERLINK", csv);
        Assert.DoesNotContain(",=HYPERLINK", csv);
    }

    private static DiagnosticsReport SampleReportWithFormula()
    {
        var telemetry = new MapperTelemetry(new TelemetryOptions { CaptureExceptionMessages = true });
        telemetry.OnFailed(typeof(RpSrc), typeof(RpDst), 1, new InvalidOperationException("=HYPERLINK(\"http://evil\")"));
        return new ReportBuilder("a").Build(Utc(2026, 10, 1), Utc(2026, 10, 2), telemetry.Snapshot(), null, Array.Empty<ProcessSample>(), Array.Empty<LiveSetObservation>(), Utc(2026, 10, 2));
    }

    [Fact]
    public void Application_names_cannot_traverse_paths()
    {
        Assert.Equal("etc-passwd", ReportRenderer.Safe("../etc/passwd"));
        Assert.Equal("app", ReportRenderer.Safe("///"));
        Assert.Equal(64, ReportRenderer.Safe(new string('a', 500)).Length);
    }

    // ------------------------------------------------------------------ delivery, retries, outbox

    private static ReportDeliveryContext Context() => new("app", Utc(2026, 10, 1), Utc(2026, 10, 2), "daily-0");
    private static IReadOnlyList<ReportFile> OneFile() => new[] { new ReportFile("AutoMapping-app-20261002T000000Z.json", "application/json", Encoding.UTF8.GetBytes("{}")) };

    [Fact]
    public void Transient_failures_are_retried_and_permanent_ones_are_not()
    {
        var delivery = new ReportDeliveryService(FastRetry(4), NullLogger.Instance);

        var flaky = new FakeSink("flaky");
        flaky.Failures.Enqueue(new IOException("network"));
        flaky.Failures.Enqueue(new ReportDeliveryException("flaky", "503", true));
        var ok = Run(delivery.DeliverToAsync(flaky, Context(), OneFile(), 4, CancellationToken.None));
        Assert.True(ok.Succeeded);
        Assert.Equal(3, ok.Attempts);

        var denied = new FakeSink("denied");
        denied.Failures.Enqueue(new ReportDeliveryException("denied", "authentication failed", false));
        var bad = Run(delivery.DeliverToAsync(denied, Context(), OneFile(), 4, CancellationToken.None));
        Assert.False(bad.Succeeded);
        Assert.Equal(1, denied.Calls);
        Assert.False(bad.Transient);

        var down = new FakeSink("down");
        for (var i = 0; i < 10; i++) down.Failures.Enqueue(new TimeoutException());
        Assert.False(Run(delivery.DeliverToAsync(down, Context(), OneFile(), 3, CancellationToken.None)).Succeeded);
        Assert.Equal(3, down.Calls);
    }

    [Fact]
    public void One_failing_sink_does_not_block_the_others()
    {
        var delivery = new ReportDeliveryService(FastRetry(2), NullLogger.Instance);
        var broken = new FakeSink("broken");
        for (var i = 0; i < 5; i++) broken.Failures.Enqueue(new ReportDeliveryException("broken", "down", false));
        var healthy = new FakeSink("healthy");
        var results = Run(delivery.DeliverAsync(new IReportSink[] { broken, healthy }, Context(), OneFile(), CancellationToken.None));
        Assert.False(results[0].Succeeded);
        Assert.True(results[1].Succeeded);
        Assert.Single(healthy.Delivered);
    }

    [Fact]
    public void Outbox_retries_later_and_gives_up_into_the_dead_letter_folder()
    {
        var dir = TempDir();
        try
        {
            var outbox = new ReportOutbox(new ReportOutbox.Options { Directory = dir, MaxAttempts = 3 }, NullLogger.Instance);
            var delivery = new ReportDeliveryService(FastRetry(1), NullLogger.Instance);
            var sink = new FakeSink("s");
            var t0 = Utc(2026, 10, 2, 1);
            outbox.Enqueue("s", Context(), OneFile(), "was down", t0);
            Assert.Equal(1, outbox.PendingCount);

            // not due yet
            var early = Run(outbox.ProcessAsync(_ => sink, delivery, t0.AddMinutes(1), CancellationToken.None));
            Assert.Equal((0, 1, 0), early);
            Assert.Equal(0, sink.Calls);

            // due and the sink works again
            var later = Run(outbox.ProcessAsync(_ => sink, delivery, t0.AddMinutes(10), CancellationToken.None));
            Assert.Equal((1, 0, 0), later);
            Assert.Equal(0, outbox.PendingCount);
            Assert.Equal("{}", Encoding.UTF8.GetString(sink.Delivered[0][0].Content));

            // a sink that never recovers ends up dead-lettered
            var broken = new FakeSink("b");
            for (var i = 0; i < 20; i++) broken.Failures.Enqueue(new IOException("still down"));
            outbox.Enqueue("b", Context(), OneFile(), "down", t0);
            var now = t0;
            var dead = 0;
            for (var i = 0; i < 6 && dead == 0; i++)
            {
                now = now.AddHours(13);
                dead += Run(outbox.ProcessAsync(_ => broken, delivery, now, CancellationToken.None)).Dead;
            }
            Assert.Equal(1, dead);
            Assert.Equal(0, outbox.PendingCount);
            Assert.True(Directory.GetDirectories(Path.Combine(dir, "dead")).Length == 1);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Local_directory_sink_writes_atomically_and_applies_retention()
    {
        var dir = TempDir();
        try
        {
            var sink = new LocalDirectorySink(dir, retentionDays: 7);
            Directory.CreateDirectory(dir);
            var old = Path.Combine(dir, "AutoMapping-old-20200101T000000Z.json");
            File.WriteAllText(old, "x");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
            var unrelated = Path.Combine(dir, "keep-me.txt");
            File.WriteAllText(unrelated, "x");
            File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-30));

            Run(sink.DeliverAsync(Context(), OneFile(), CancellationToken.None));

            Assert.True(File.Exists(Path.Combine(dir, "AutoMapping-app-20261002T000000Z.json")));
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(unrelated));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

            var evil = new[] { new ReportFile("../escape.json", "application/json", new byte[] { 1 }) };
            var ex = Assert.Throws<ReportDeliveryException>(() => Run(sink.DeliverAsync(Context(), evil, CancellationToken.None)));
            Assert.False(ex.IsTransient);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Webhook_sink_signs_classifies_errors_and_requires_https()
    {
        var handler = new StubHandler();
        var sink = new WebhookSink(new Uri("https://hooks.example.com/AutoMapping?token=SECRET"), "s3cret", new HttpClient(handler));
        Run(sink.DeliverAsync(Context(), OneFile(), CancellationToken.None));
        var timestamp = handler.Last!.Headers.GetValues("X-AutoMapping-Timestamp").Single();
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"), Encoding.UTF8.GetBytes(timestamp + ".{}"))).ToLowerInvariant();
        Assert.Equal(expected, handler.Last!.Headers.GetValues("X-AutoMapping-Signature").Single());
        Assert.DoesNotContain("SECRET", sink.ToString());   // tokens in the URL stay out of logs

        handler.Status = HttpStatusCode.InternalServerError;
        Assert.True(Assert.Throws<ReportDeliveryException>(() => Run(sink.DeliverAsync(Context(), OneFile(), CancellationToken.None))).IsTransient);
        handler.Status = HttpStatusCode.Unauthorized;
        Assert.False(Assert.Throws<ReportDeliveryException>(() => Run(sink.DeliverAsync(Context(), OneFile(), CancellationToken.None))).IsTransient);
        handler.Status = HttpStatusCode.TooManyRequests;
        Assert.True(Assert.Throws<ReportDeliveryException>(() => Run(sink.DeliverAsync(Context(), OneFile(), CancellationToken.None))).IsTransient);

        Assert.Throws<ReportingConfigurationException>(() => new WebhookSink(new Uri("http://insecure.example.com/")));
    }

    // ------------------------------------------------------------------ the scheduler

    private static ReportScheduler Scheduler(ReportingOptions options, ManualTime time, IReportStateStore? state = null, MapperConfiguration? config = null)
    {
        config ??= NewConfig();
        return new ReportScheduler(options, config, new MapperTelemetry(), new ProcessSampler(options.Sampler, () => config.GetCacheStatistics(), time),
            new ReportingStatus(), new NoServices(), NullLogger<ReportScheduler>.Instance, time, state ?? new InMemoryReportStateStore());
    }

    [Fact]
    public void Scheduler_produces_each_due_report_once_and_delivers_it()
    {
        var sink = new FakeSink("a");
        var options = new ReportingOptions { Application = "orders", StateDirectory = TempDir() }.Daily(new TimeOnly(6, 0)).AddSink(sink);
        var time = new ManualTime { Now = Utc(2026, 10, 26, 5, 0) };
        var scheduler = Scheduler(options, time);

        Assert.Empty(Run(scheduler.RunOnceAsync(time.Now, CancellationToken.None)));             // 05:00, not yet due
        time.Now = Utc(2026, 10, 26, 6, 1);
        var first = Run(scheduler.RunOnceAsync(time.Now, CancellationToken.None));
        Assert.Single(first);
        Assert.Equal(Utc(2026, 10, 25, 6), first[0].PeriodStart);
        Assert.Equal(Utc(2026, 10, 26, 6), first[0].PeriodEnd);
        Assert.Equal(2, sink.Delivered[0].Count);                                                // html + json by default

        Assert.Empty(Run(scheduler.RunOnceAsync(Utc(2026, 10, 26, 7, 0), CancellationToken.None)));   // same run is never repeated
        Assert.Single(sink.Delivered);
        Assert.Single(Run(scheduler.RunOnceAsync(Utc(2026, 10, 27, 6, 5), CancellationToken.None)));   // next day
        Assert.Equal(2, sink.Delivered.Count);
    }

    [Fact]
    public void Scheduler_catches_up_within_grace_and_skips_when_too_late()
    {
        var state = new InMemoryReportStateStore();
        var options = new ReportingOptions { Application = "x", StateDirectory = TempDir(), CatchUpGrace = TimeSpan.FromHours(6) }.Daily(new TimeOnly(6, 0)).AddSink(new FakeSink("a"));
        var time = new ManualTime { Now = Utc(2026, 10, 26, 0, 0) };
        var scheduler = Scheduler(options, time, state);
        state.SetLastRun("Daily-0", Utc(2026, 10, 24, 6, 0));                                      // last ran on the 24th

        // process was down: it is now the 26th 08:00. The 25th 06:00 report is 26h late (skipped), the 26th 06:00 one is 2h late (kept)
        var produced = Run(scheduler.RunOnceAsync(Utc(2026, 10, 26, 8, 0), CancellationToken.None));
        Assert.Single(produced);
        Assert.Equal(Utc(2026, 10, 26, 6), produced[0].PeriodEnd);
        Assert.Equal(Utc(2026, 10, 26, 6), state.GetLastRun("Daily-0"));
    }

    [Fact]
    public void Scheduler_supports_several_schedules_and_sinks_per_preference()
    {
        var all = new FakeSink("all");
        var monthlyOnly = new FakeSink("archive");
        var options = new ReportingOptions { Application = "x", StateDirectory = TempDir() }
            .Daily(new TimeOnly(6, 0), formats: new[] { ReportFormat.Markdown }, sinks: new[] { "all" })
            .Weekly(DayOfWeek.Monday, new TimeOnly(6, 0), formats: new[] { ReportFormat.Html }, sinks: new[] { "all", "archive" })
            .Monthly(2, new TimeOnly(6, 0), formats: new[] { ReportFormat.Json, ReportFormat.Csv }, sinks: new[] { "archive" })
            .AddSink(all).AddSink(monthlyOnly);
        var time = new ManualTime { Now = Utc(2026, 10, 31, 12, 0) };
        var state = new InMemoryReportStateStore();
        state.SetLastRun("Daily-0", Utc(2026, 11, 1, 6));
        state.SetLastRun("Weekly-0", Utc(2026, 10, 26, 6));
        state.SetLastRun("Monthly-0", Utc(2026, 10, 2, 6));
        var scheduler = Scheduler(options, time, state);

        var produced = Run(scheduler.RunOnceAsync(Utc(2026, 11, 2, 7, 0), CancellationToken.None));   // Monday 2 Nov: the day's, the week's and the month's reports
        Assert.Equal(3, produced.Count);
        Assert.Equal(2, all.Delivered.Count);          // daily + weekly
        Assert.Equal(2, monthlyOnly.Delivered.Count);  // weekly + monthly
        Assert.Contains(monthlyOnly.Delivered, files => files.Count == 3);   // json + two csv files
    }

    [Fact]
    public void Failed_delivery_goes_to_the_outbox_and_is_retried_by_a_later_tick()
    {
        var dir = TempDir();
        try
        {
            var sink = new FakeSink("a");
            sink.Failures.Enqueue(new ReportDeliveryException("a", "down", false));
            var options = new ReportingOptions { Application = "x", StateDirectory = dir, Retry = { MaxAttempts = 1 } }.Daily(new TimeOnly(6, 0)).AddSink(sink);
            var time = new ManualTime { Now = Utc(2026, 10, 26, 6, 1) };
            var state = new InMemoryReportStateStore();
            state.SetLastRun("Daily-0", Utc(2026, 10, 25, 6));
            var scheduler = Scheduler(options, time, state);

            Run(scheduler.RunOnceAsync(time.Now, CancellationToken.None));
            Assert.False(scheduler.Status.Healthy);
            Assert.Equal(1, scheduler.Status.OutboxPending);
            Assert.Empty(sink.Delivered);

            Run(scheduler.RunOnceAsync(Utc(2026, 10, 26, 6, 3), CancellationToken.None));           // too early for the retry (5 minutes)
            Assert.Empty(sink.Delivered);
            Run(scheduler.RunOnceAsync(Utc(2026, 10, 26, 6, 30), CancellationToken.None));
            Assert.Single(sink.Delivered);
            Assert.True(scheduler.Status.Healthy);
            Assert.Equal(0, scheduler.Status.OutboxPending);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Scheduler_attaches_and_detaches_without_disturbing_other_observers()
    {
        var config = NewConfig();
        var other = new MapperTelemetry();
        config.Observer = other;
        var options = new ReportingOptions { Application = "x", StateDirectory = TempDir() }.Daily(new TimeOnly(6, 0)).AddSink(new FakeSink("a"));
        var scheduler = Scheduler(options, new ManualTime(), config: config);
        scheduler.Attach();
        scheduler.Attach();   // idempotent

        config.CreateMapper().Map<RpDst>(new RpSrc());
        Assert.Equal(1, other.Snapshot().Pairs[0].Calls);
        Run(scheduler.StopAsync(CancellationToken.None));
        Assert.Same(other, config.Observer);
    }

    [Fact]
    public void Bad_configuration_fails_at_startup()
    {
        Assert.Throws<ReportingConfigurationException>(() => ValidateOnly(new ReportingOptions().AddSink(new FakeSink("a"))));   // no schedule
        Assert.Throws<ReportingConfigurationException>(() => ValidateOnly(new ReportingOptions().Daily(new TimeOnly(1, 0))));     // no sink
        var duplicate = new ReportingOptions { StateDirectory = TempDir() }.Daily(new TimeOnly(1, 0)).AddSink(new FakeSink("a")).AddSink(new FakeSink("a"));
        Assert.Throws<ReportingConfigurationException>(() => Scheduler(duplicate, new ManualTime()));
        var unknown = new ReportingOptions { StateDirectory = TempDir() }.Daily(new TimeOnly(1, 0), sinks: new[] { "nope" }).AddSink(new FakeSink("a"));
        Assert.Throws<ReportingConfigurationException>(() => Scheduler(unknown, new ManualTime()));
    }

    private static void ValidateOnly(ReportingOptions options)
    {
        try { typeof(ReportingOptions).GetMethod("Validate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(options, null); }
        catch (System.Reflection.TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); }
    }

    [Fact]
    public void State_file_survives_restart_and_corruption()
    {
        var dir = TempDir();
        try
        {
            var store = new FileReportStateStore(dir, NullLogger.Instance);
            store.SetLastRun("Daily-0", Utc(2026, 10, 25, 6));
            Assert.Equal(Utc(2026, 10, 25, 6), new FileReportStateStore(dir, NullLogger.Instance).GetLastRun("Daily-0"));

            File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json");
            Assert.Null(new FileReportStateStore(dir, NullLogger.Instance).GetLastRun("Daily-0"));   // starts fresh instead of crashing
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Process_sampler_keeps_fixed_size_buffers()
    {
        var time = new ManualTime();
        var sampler = new ProcessSampler(new SamplerOptions { FineCapacity = 5, CoarseCapacity = 3, LiveSetCapacity = 4 }, () => default, time);
        for (var i = 0; i < 50; i++) { time.Now = time.Now.AddMinutes(30); sampler.Sample(); sampler.AddLiveSetObservation(new LiveSetObservation(time.Now, i)); }
        var all = sampler.Samples(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Assert.True(all.Count <= 5);
        Assert.True(sampler.LiveSet(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Count <= 4);
    }

    [Fact]
    public void Hosted_service_starts_samples_and_stops_cleanly()
    {
        var config = NewConfig();
        var options = new ReportingOptions { Application = "x", StateDirectory = TempDir() }.Daily(new TimeOnly(6, 0)).AddSink(new FakeSink("a"));
        var scheduler = new ReportScheduler(options, config, new MapperTelemetry(), new ProcessSampler(options.Sampler, () => config.GetCacheStatistics()),
            new ReportingStatus(), new NoServices(), NullLogger<ReportScheduler>.Instance);
        try
        {
            Run(scheduler.StartAsync(CancellationToken.None));
            SpinWait.SpinUntil(() => config.Observer != null, TimeSpan.FromSeconds(5));
            Assert.NotNull(config.Observer);
            config.CreateMapper().Map<RpDst>(new RpSrc());

            var stopped = scheduler.StopAsync(CancellationToken.None);
            Assert.True(stopped.Wait(TimeSpan.FromSeconds(10)));
            Assert.Null(config.Observer);
        }
        finally { if (Directory.Exists(options.StateDirectory)) Directory.Delete(options.StateDirectory, true); }
    }
}
