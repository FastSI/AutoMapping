using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoMapping;
using AutoMapping.Alerting;
using AutoMapping.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutoMapping.Tests;

internal sealed class RecordingAlertSink : IAlertSink
{
    public RecordingAlertSink(string name = "rec") => Name = name;
    public string Name { get; }
    public List<Alert> Alerts { get; } = new();
    public Queue<Exception> Failures { get; } = new();
    public int Calls;
    public Task SendAsync(Alert alert, CancellationToken ct)
    {
        Calls++;
        if (Failures.Count > 0) throw Failures.Dequeue();
        Alerts.Add(alert);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSmtp : ISmtpSender
{
    public SmtpAlertOptions? Options;
    public string? Subject, Body;
    public List<string> To { get; } = new();
    public Exception? Throw;
    public Task SendAsync(SmtpAlertOptions options, MailMessage message, CancellationToken ct)
    {
        if (Throw != null) throw Throw;
        Options = options;
        Subject = message.Subject;
        Body = message.Body;
        To.AddRange(message.To.Select(a => a.Address));
        return Task.CompletedTask;
    }
}

public class AlertingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlertThresholds Defaults = new();
    private static readonly LeakDetectionOptions Leak = new();

    // bucket i holds durations in [2^i, 2^(i+1)) microseconds, the percentile reports 2^(i+1)
    private static long[] Hist(int bucket, long count) { var h = new long[32]; h[bucket] = count; return h; }

    private static PairSnapshot Pair(long calls, long errors, int bucket = 3) =>
        new("Order", "OrderDto", calls, errors, calls * 0.01, 0.1, Hist(bucket, calls));

    private static TelemetrySnapshot Snap(DateTimeOffset at, PairSnapshot[]? pairs = null, CompileSnapshot[]? compiles = null) =>
        new(at, pairs ?? Array.Empty<PairSnapshot>(), Array.Empty<ExceptionSnapshot>(), compiles ?? Array.Empty<CompileSnapshot>(), 0, 0);

    private static IReadOnlyList<AlertCondition> Eval(TelemetrySnapshot current, TelemetrySnapshot? baseline = null,
        IReadOnlyList<ProcessSample>? samples = null, IReadOnlyList<LiveSetObservation>? liveSet = null, int deliveryFailures = 0,
        AlertThresholds? thresholds = null) =>
        AlertEvaluator.Evaluate(thresholds ?? Defaults, Leak, current, baseline, samples ?? Array.Empty<ProcessSample>(),
            liveSet ?? Array.Empty<LiveSetObservation>(), deliveryFailures);

    private static ProcessSample Sample(DateTimeOffset at, double gc = 0, int typeMaps = 10, int mappings = 10, int projections = 0) =>
        new(at, 1, 1, 1, 0, 0, 0, gc, 1, 0, new MapperCacheStatistics(typeMaps, mappings, projections, 0, 0));

    // ---------------------------------------------------------------- evaluator

    [Fact]
    public void Quiet_system_raises_nothing() =>
        Assert.Empty(Eval(Snap(T0, new[] { Pair(1000, 0) })));

    [Fact]
    public void Error_rate_warning_and_critical_use_the_window_delta_not_the_lifetime_total()
    {
        var baseline = Snap(T0, new[] { Pair(10_000, 9_000) });          // old failures must not count
        var warning = Eval(Snap(T0.AddMinutes(5), new[] { Pair(10_200, 9_002) }), baseline);   // 2 of 200 = 1%
        var c = Assert.Single(warning);
        Assert.Equal(AlertKind.ErrorRate, c.Kind);
        Assert.Equal(AlertSeverity.Warning, c.Severity);

        var critical = Eval(Snap(T0.AddMinutes(5), new[] { Pair(10_200, 9_020) }), baseline);  // 10%
        Assert.Equal(AlertSeverity.Critical, Assert.Single(critical).Severity);

        Assert.Empty(Eval(Snap(T0.AddMinutes(5), new[] { Pair(10_200, 9_000) }), baseline));   // no new errors
    }

    [Fact]
    public void Pairs_with_too_few_calls_are_not_judged()
    {
        // 5 of 10 failed, but 10 calls is below MinCallsForRates: too little data to call it a trend
        Assert.Empty(Eval(Snap(T0, new[] { Pair(10, 5) })));
    }

    [Fact]
    public void Slow_p95_warns_then_goes_critical()
    {
        var warning = Assert.Single(Eval(Snap(T0, new[] { Pair(500, 0, bucket: 12) })));   // 4096-8191 us -> reported 8192
        Assert.Equal(AlertKind.SlowMapping, warning.Kind);
        Assert.Equal(AlertSeverity.Warning, warning.Severity);

        var critical = Assert.Single(Eval(Snap(T0, new[] { Pair(500, 0, bucket: 13) })));  // reported 16384
        Assert.Equal(AlertSeverity.Critical, critical.Severity);
    }

    [Fact]
    public void Slow_compile_uses_the_average_of_compiles_in_the_window()
    {
        var baseline = Snap(T0, compiles: new[] { new CompileSnapshot("A", "B", 1, 1000, 1000) });
        var slow = Eval(Snap(T0.AddMinutes(5), compiles: new[] { new CompileSnapshot("A", "B", 3, 2400, 1000) }), baseline);   // 1400 ms over 2
        Assert.Equal(AlertKind.SlowCompile, Assert.Single(slow).Kind);

        var quick = Eval(Snap(T0.AddMinutes(5), compiles: new[] { new CompileSnapshot("A", "B", 3, 1100, 1000) }), baseline);   // 100 ms over 2
        Assert.Empty(quick);
    }

    [Fact]
    public void Gc_pause_and_cache_growth_come_from_the_samples()
    {
        var samples = new[] { Sample(T0, gc: 2, typeMaps: 10), Sample(T0.AddMinutes(5), gc: 35, typeMaps: 20) };
        var result = Eval(Snap(T0.AddMinutes(5)), samples: samples);
        Assert.Contains(result, c => c.Kind == AlertKind.GcPause && c.Severity == AlertSeverity.Critical);
        Assert.Contains(result, c => c.Kind == AlertKind.CacheGrowth);

        Assert.Empty(Eval(Snap(T0), samples: new[] { Sample(T0, gc: 2), Sample(T0.AddMinutes(5), gc: 3) }));
    }

    [Fact]
    public void A_steadily_rising_live_set_is_reported_as_a_suspected_leak()
    {
        var rising = Enumerable.Range(0, 10).Select(i => new LiveSetObservation(T0.AddMinutes(i * 15), 100_000_000L + i * 20_000_000L)).ToList();
        Assert.Contains(Eval(Snap(T0), liveSet: rising), c => c.Kind == AlertKind.MemoryLeak);

        var flat = Enumerable.Range(0, 10).Select(i => new LiveSetObservation(T0.AddMinutes(i * 15), 100_000_000L)).ToList();
        Assert.DoesNotContain(Eval(Snap(T0), liveSet: flat), c => c.Kind == AlertKind.MemoryLeak);
    }

    [Fact]
    public void Repeated_report_delivery_failures_raise_an_alert()
    {
        Assert.Empty(Eval(Snap(T0), deliveryFailures: 2));
        Assert.Equal(AlertSeverity.Warning, Assert.Single(Eval(Snap(T0), deliveryFailures: 3)).Severity);
        Assert.Equal(AlertSeverity.Critical, Assert.Single(Eval(Snap(T0), deliveryFailures: 10)).Severity);
    }

    [Fact]
    public void Alert_text_holds_type_names_and_numbers_only()
    {
        var c = Assert.Single(Eval(Snap(T0, new[] { Pair(200, 100) })));
        Assert.Contains("Order -> OrderDto", c.Title);
        Assert.DoesNotContain("Exception", c.Message);
    }

    // ---------------------------------------------------------------- manager

    private static AlertCondition Cond(string key = "k", AlertSeverity severity = AlertSeverity.Warning) =>
        new(key, AlertKind.ErrorRate, severity, "title " + key, "message", "A", "B", 1, 1);

    private static AlertOptions FastOptions() => new() { Retry = { MaxAttempts = 3, Delay = (_, _) => Task.CompletedTask, Jitter = 0 } };

    private static AlertManager Manager(AlertOptions options, params IAlertSink[] sinks) =>
        new(options, sinks, "app", NullLogger.Instance);

    private static Task Step(AlertManager m, DateTimeOffset at, params AlertCondition[] conditions) =>
        m.ProcessAsync(conditions, at, CancellationToken.None);

    [Fact]
    public async Task A_new_condition_is_sent_once_and_not_repeated_within_the_cooldown()
    {
        var sink = new RecordingAlertSink();
        var m = Manager(FastOptions(), sink);

        await Step(m, T0, Cond());
        await Step(m, T0.AddMinutes(1), Cond());
        await Step(m, T0.AddMinutes(10), Cond());

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertState.Firing, alert.State);
        Assert.Equal("app", alert.Application);
        Assert.Single(m.ActiveAlerts(T0));
    }

    [Fact]
    public async Task A_condition_that_stays_is_announced_again_after_the_cooldown()
    {
        var sink = new RecordingAlertSink();
        var options = FastOptions();
        options.Cooldown = TimeSpan.FromMinutes(30);
        var m = Manager(options, sink);

        await Step(m, T0, Cond());
        await Step(m, T0.AddMinutes(31), Cond());

        Assert.Equal(2, sink.Alerts.Count);
        Assert.Equal(T0, sink.Alerts[1].FiringSince);
    }

    [Fact]
    public async Task Escalation_to_critical_is_sent_immediately()
    {
        var sink = new RecordingAlertSink();
        var m = Manager(FastOptions(), sink);

        await Step(m, T0, Cond(severity: AlertSeverity.Warning));
        await Step(m, T0.AddMinutes(1), Cond(severity: AlertSeverity.Critical));

        Assert.Equal(new[] { AlertSeverity.Warning, AlertSeverity.Critical }, sink.Alerts.Select(a => a.Severity));
    }

    [Fact]
    public async Task A_condition_resolves_only_after_it_has_been_gone_for_ClearEvaluations_checks()
    {
        var sink = new RecordingAlertSink();
        var options = FastOptions();
        options.ClearEvaluations = 2;
        var m = Manager(options, sink);

        await Step(m, T0, Cond());
        await Step(m, T0.AddMinutes(1));                 // gone once: still active
        Assert.Single(m.ActiveAlerts(T0));
        await Step(m, T0.AddMinutes(2), Cond());         // came back: the count starts again
        await Step(m, T0.AddMinutes(3));
        await Step(m, T0.AddMinutes(4));                 // gone twice in a row: resolved

        Assert.Equal(new[] { AlertState.Firing, AlertState.Resolved }, sink.Alerts.Select(a => a.State));
        Assert.Empty(m.ActiveAlerts(T0));
    }

    [Fact]
    public async Task Resolved_notifications_can_be_turned_off()
    {
        var sink = new RecordingAlertSink();
        var options = FastOptions();
        options.ClearEvaluations = 1;
        options.NotifyOnResolved = false;
        var m = Manager(options, sink);

        await Step(m, T0, Cond());
        await Step(m, T0.AddMinutes(1));

        Assert.Single(sink.Alerts);
        Assert.Empty(m.ActiveAlerts(T0));
    }

    [Fact]
    public async Task Alerts_below_the_minimum_severity_are_ignored()
    {
        var sink = new RecordingAlertSink();
        var options = FastOptions();
        options.MinSeverity = AlertSeverity.Critical;
        var m = Manager(options, sink);

        await Step(m, T0, Cond("w", AlertSeverity.Warning), Cond("c", AlertSeverity.Critical));

        Assert.Equal("c", Assert.Single(sink.Alerts).Key);
    }

    [Fact]
    public async Task The_number_of_tracked_conditions_is_bounded()
    {
        var sink = new RecordingAlertSink();
        var options = FastOptions();
        options.MaxActiveAlerts = 2;
        var m = Manager(options, sink);

        await Step(m, T0, Cond("a"), Cond("b"), Cond("c"), Cond("d"));

        Assert.Equal(2, sink.Alerts.Count);
        Assert.Equal(2, m.Dropped);
    }

    [Fact]
    public async Task A_failing_sink_never_throws_and_never_blocks_the_others()
    {
        var broken = new RecordingAlertSink("broken");
        for (var i = 0; i < 10; i++) broken.Failures.Enqueue(new InvalidOperationException("permanent"));
        var good = new RecordingAlertSink("good");
        var m = Manager(FastOptions(), broken, good);

        await Step(m, T0, Cond());

        Assert.Equal(1, broken.Calls);     // a permanent error is not retried
        Assert.Single(good.Alerts);
    }

    [Fact]
    public async Task A_transient_failure_is_retried()
    {
        var flaky = new RecordingAlertSink();
        flaky.Failures.Enqueue(new HttpRequestException("network"));
        flaky.Failures.Enqueue(new TimeoutException());
        var m = Manager(FastOptions(), flaky);

        await Step(m, T0, Cond());

        Assert.Equal(3, flaky.Calls);
        Assert.Single(flaky.Alerts);
    }

    [Fact]
    public async Task Shutdown_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var m = Manager(FastOptions(), new RecordingAlertSink());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.ProcessAsync(new[] { Cond() }, T0, cts.Token));
    }

    // ---------------------------------------------------------------- sinks

    private static Alert SampleAlert(string title = "High error rate: A -> B") =>
        new("k", AlertKind.ErrorRate, AlertSeverity.Critical, AlertState.Firing, title, "details", "A", "B", 0.1, 0.05, T0, T0, "orders-api");

    [Fact]
    public async Task Webhook_sends_signed_json_in_the_same_scheme_as_report_webhooks()
    {
        var handler = new StubHandler();
        var sink = new WebhookAlertSink(new Uri("https://hooks.example.com/x"), "s3cret", new HttpClient(handler));

        await sink.SendAsync(SampleAlert(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Critical", doc.RootElement.GetProperty("severity").GetString());
        Assert.Equal("Firing", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("orders-api", doc.RootElement.GetProperty("application").GetString());

        var timestamp = handler.Last.Headers.GetValues("X-AutoMapping-Timestamp").Single();
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("s3cret"),
            Encoding.UTF8.GetBytes(timestamp + ".").Concat(handler.Body!).ToArray())).ToLowerInvariant();
        Assert.Equal(expected, handler.Last.Headers.GetValues("X-AutoMapping-Signature").Single());
    }

    [Fact]
    public async Task Webhook_without_a_secret_sends_no_signature_and_server_errors_are_transient()
    {
        var handler = new StubHandler { Status = HttpStatusCode.ServiceUnavailable };
        var sink = new WebhookAlertSink(new Uri("https://hooks.example.com/x"), client: new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<ReportDeliveryException>(() => sink.SendAsync(SampleAlert(), CancellationToken.None));
        Assert.True(ex.IsTransient);
        Assert.False(handler.Last!.Headers.Contains("X-AutoMapping-Signature"));

        handler.Status = HttpStatusCode.Unauthorized;
        Assert.False((await Assert.ThrowsAsync<ReportDeliveryException>(() => sink.SendAsync(SampleAlert(), CancellationToken.None))).IsTransient);
    }

    [Fact]
    public void Webhook_requires_https_and_never_prints_the_path()
    {
        Assert.Throws<AlertConfigurationException>(() => new WebhookAlertSink(new Uri("http://hooks.example.com/x")));
        var sink = new WebhookAlertSink(new Uri("https://hooks.example.com/services/T000/SECRET"));
        Assert.DoesNotContain("SECRET", sink.ToString());
    }

    [Fact]
    public async Task Email_has_one_line_subject_and_never_the_password()
    {
        var smtp = new RecordingSmtp();
        var options = new SmtpAlertOptions { Host = "smtp.example.com", From = "alerts@example.com", UserName = "u", Password = "p4ss" };
        options.To.Add("ops@example.com");
        var sink = new SmtpAlertSink(options, smtp);

        await sink.SendAsync(SampleAlert("Title\r\nBcc: attacker@example.com"), CancellationToken.None);

        Assert.DoesNotContain('\n', smtp.Subject!);
        Assert.DoesNotContain('\r', smtp.Subject!);
        Assert.StartsWith("[AutoMapping] CRITICAL:", smtp.Subject);
        Assert.Equal(new[] { "ops@example.com" }, smtp.To);
        Assert.Contains("orders-api", smtp.Body);
        Assert.DoesNotContain("p4ss", sink.ToString() + smtp.Subject + smtp.Body);
        Assert.True(options.UseSsl);   // secure by default
    }

    [Fact]
    public async Task Email_server_refusals_are_classified_and_do_not_echo_server_text()
    {
        var smtp = new RecordingSmtp();
        var options = new SmtpAlertOptions { Host = "smtp.example.com", From = "alerts@example.com" };
        options.To.Add("ops@example.com");
        var sink = new SmtpAlertSink(options, smtp);

        smtp.Throw = new SmtpException(SmtpStatusCode.MailboxUnavailable, "550 user secret-name unknown");
        var permanent = await Assert.ThrowsAsync<ReportDeliveryException>(() => sink.SendAsync(SampleAlert(), CancellationToken.None));
        Assert.False(permanent.IsTransient);
        Assert.DoesNotContain("secret-name", permanent.Message);

        smtp.Throw = new SmtpException(SmtpStatusCode.ServiceNotAvailable);
        Assert.True((await Assert.ThrowsAsync<ReportDeliveryException>(() => sink.SendAsync(SampleAlert(), CancellationToken.None))).IsTransient);
    }

    [Fact]
    public void Email_configuration_is_validated_up_front()
    {
        Assert.Throws<AlertConfigurationException>(() => new SmtpAlertSink(new SmtpAlertOptions { From = "a@b.com" }));   // no host
        var noRecipient = new SmtpAlertOptions { Host = "h", From = "a@b.com" };
        Assert.Throws<AlertConfigurationException>(() => new SmtpAlertSink(noRecipient));
        var badAddress = new SmtpAlertOptions { Host = "h", From = "not an address" };
        badAddress.To.Add("ops@example.com");
        Assert.Throws<AlertConfigurationException>(() => new SmtpAlertSink(badAddress));
    }

    [Fact]
    public async Task Callback_sink_receives_the_alert()
    {
        Alert? seen = null;
        var sink = new CallbackAlertSink((a, _) => { seen = a; return Task.CompletedTask; });
        await sink.SendAsync(SampleAlert(), CancellationToken.None);
        Assert.Equal("k", seen!.Key);
    }

    // ---------------------------------------------------------------- options, service, DI

    [Fact]
    public void Options_fail_fast_when_unusable()
    {
        Assert.Throws<AlertConfigurationException>(() => new ServiceCollection().AddAutoMappingAlerts(_ => { }));                 // no sink
        Assert.Throws<AlertConfigurationException>(() => new ServiceCollection().AddAutoMappingAlerts(o => { o.AddLog(); o.EvaluationInterval = TimeSpan.FromSeconds(1); }));
        Assert.Throws<AlertConfigurationException>(() => new ServiceCollection().AddAutoMappingAlerts(o => { o.AddLog(); o.Thresholds.ErrorRateCritical = 0.001; }));
        Assert.Throws<AlertConfigurationException>(() => new ServiceCollection().AddAutoMappingAlerts(o => { o.AddLog(); o.ClearEvaluations = 0; }));
    }

    [Fact]
    public void Alerts_without_reporting_say_what_is_missing()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAutoMappingAlerts(o => o.AddLog());
        using var provider = services.BuildServiceProvider();
        var ex = Assert.Throws<AlertConfigurationException>(() => provider.GetRequiredService<AlertService>());
        Assert.Contains("AddAutoMappingReporting", ex.Message);
    }

    [Fact]
    public void Alerts_register_next_to_reporting_without_changing_it()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddAutoMapping(cfg => cfg.CreateMap<RpSrc, RpDst>());
        services.AddAutoMappingReporting(o => { o.AddLocalDirectory(Path.GetTempPath()); o.Daily(new TimeOnly(5, 0)); });
        services.AddAutoMappingAlerts(o => o.AddLog());
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<AlertService>());
        Assert.NotNull(provider.GetRequiredService<ReportScheduler>());
        Assert.Equal(2, provider.GetServices<IHostedService>().Count());
    }

    [Fact]
    public async Task Service_alerts_on_real_telemetry_and_resolves_when_it_recovers()
    {
        var time = new ManualTime { Now = T0 };
        var telemetry = new MapperTelemetry(null, time);
        var reporting = new ReportingOptions();
        var sampler = new ProcessSampler(reporting.Sampler, () => default);
        var sink = new RecordingAlertSink();
        var options = FastOptions();
        options.AddSink(sink);
        options.Window = TimeSpan.FromMinutes(5);
        options.ClearEvaluations = 1;
        var service = new AlertService(options, reporting, telemetry, sampler, new ReportingStatus(), new NoServices(),
            NullLogger<AlertService>.Instance);

        await service.EvaluateOnceAsync(T0, CancellationToken.None);   // first call only records a starting point
        Assert.Empty(sink.Alerts);

        var ticks = System.Diagnostics.Stopwatch.Frequency / 20_000;   // 50 microseconds: fast, so only the error rate can alert
        time.Now = T0.AddMinutes(1);
        for (var i = 0; i < 200; i++) telemetry.OnFailed(typeof(RpSrc), typeof(RpDst), ticks, new InvalidOperationException("boom"));
        await service.EvaluateOnceAsync(time.Now, CancellationToken.None);

        var alert = Assert.Single(sink.Alerts);
        Assert.Equal(AlertKind.ErrorRate, alert.Kind);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.DoesNotContain("boom", alert.Message);                  // exception text never leaves the process
        Assert.Single(service.ActiveAlerts);

        // 6 quiet minutes later the old failures are outside the window and a healthy stream follows
        time.Now = T0.AddMinutes(7);
        for (var i = 0; i < 200; i++) telemetry.OnMapped(typeof(RpSrc), typeof(RpDst), ticks);
        await service.EvaluateOnceAsync(time.Now, CancellationToken.None);
        time.Now = T0.AddMinutes(13);
        for (var i = 0; i < 200; i++) telemetry.OnMapped(typeof(RpSrc), typeof(RpDst), ticks);
        await service.EvaluateOnceAsync(time.Now, CancellationToken.None);

        Assert.Equal(AlertState.Resolved, sink.Alerts[^1].State);
        Assert.Empty(service.ActiveAlerts);
    }

    [Fact]
    public async Task Mapping_results_are_identical_with_and_without_alerts_running()
    {
        var plain = new MapperConfiguration(c => c.CreateMap<RpSrc, RpDst>()).CreateMapper();
        var observed = new MapperConfiguration(c => c.CreateMap<RpSrc, RpDst>());
        var telemetry = new MapperTelemetry();
        observed.Observer = telemetry;
        var mapper = observed.CreateMapper();
        var options = FastOptions();
        options.AddSink(new RecordingAlertSink());
        var service = new AlertService(options, new ReportingOptions(), telemetry,
            new ProcessSampler(new SamplerOptions(), () => default), new ReportingStatus(), new NoServices(), NullLogger<AlertService>.Instance);

        for (var i = 0; i < 50; i++)
        {
            var src = new RpSrc { Id = i, Name = "n" + i };
            await service.EvaluateOnceAsync(T0.AddSeconds(i), CancellationToken.None);
            var a = plain.Map<RpDst>(src);
            var b = mapper.Map<RpDst>(src);
            Assert.Equal((a.Id, a.Name), (b.Id, b.Name));
        }
    }
}
