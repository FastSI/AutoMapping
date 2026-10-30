using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoMapping.Reporting;

/// <summary>
/// Hosted service that samples the process, produces the configured reports on schedule and delivers them. It never lets an
/// exception escape (a crashed background service would take the host down) and never touches the mapping hot path.
/// </summary>
public sealed class ReportScheduler : BackgroundService
{
    private readonly ReportingOptions _options;
    private readonly MapperConfiguration _config;
    private readonly MapperTelemetry _telemetry;
    private readonly ProcessSampler _sampler;
    private readonly ReportingStatus _status;
    private readonly ILogger<ReportScheduler> _logger;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<IReportSink> _sinks;
    private readonly ReportBuilder _builder;
    private readonly ReportDeliveryService _delivery;
    private readonly ReportOutbox _outbox;
    private readonly IReportStateStore _state;
    private readonly Dictionary<string, TelemetrySnapshot?> _baselines = new();
    private readonly Dictionary<string, DateTimeOffset> _blockedUntil = new();
    private readonly DateTimeOffset _startedAt;

    public ReportScheduler(ReportingOptions options, MapperConfiguration config, MapperTelemetry telemetry, ProcessSampler sampler,
        ReportingStatus status, IServiceProvider services, ILogger<ReportScheduler> logger, TimeProvider? timeProvider = null,
        IReportStateStore? stateStore = null)
    {
        _options = options;
        _config = config;
        _telemetry = telemetry;
        _sampler = sampler;
        _status = status;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow();
        _sinks = options.SinkFactories.Select(f => f(services)).ToList();
        var names = _sinks.Select(s => s.Name).ToList();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new ReportingConfigurationException("Every sink needs a unique name (pass name: ... when adding two of the same kind).");
        foreach (var schedule in options.Schedules.Where(s => s.SinkNames != null))
            foreach (var wanted in schedule.SinkNames!.Where(n => !names.Contains(n, StringComparer.OrdinalIgnoreCase)))
                throw new ReportingConfigurationException($"The {schedule.Schedule.Frequency} schedule names the sink '{wanted}', which is not registered.");

        _builder = new ReportBuilder(options.Application, options.Thresholds, options.IncludeHostName);
        _delivery = new ReportDeliveryService(options.Retry, logger);
        var directory = options.ResolveStateDirectory();
        _outbox = new ReportOutbox(new ReportOutbox.Options { Directory = Path.Combine(directory, "outbox"), MaxAge = options.OutboxMaxAge }, logger);
        _state = stateStore ?? new FileReportStateStore(directory, logger);
    }

    public ReportingStatus Status => _status;

    /// <summary>Attaches the telemetry to the mapper. Idempotent.</summary>
    public void Attach()
    {
        var existing = _config.Observer;
        if (existing == null) _config.Observer = _telemetry;
        else if (!ReferenceEquals(existing, _telemetry) && !(existing is CompositeObserver c && c.Contains(_telemetry)))
            _config.Observer = new CompositeObserver(existing, _telemetry);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Attach();
        _logger.LogInformation("AutoMapping reporting started for '{Application}': {Schedules}.", _options.Application,
            string.Join("; ", _options.Schedules.Select(s => s.Schedule.ToString())));

        var sampling = SampleLoopAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RunOnceAsync(_time.GetUtcNow(), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    // the loop must survive anything: log, remember, try again next tick
                    _status.Failed(_time.GetUtcNow(), ReportDeliveryService.Describe(ex));
                    _logger.LogError(ex, "Reporting tick failed; will try again.");
                }
                try { await Task.Delay(TimeSpan.FromMinutes(1), _time, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            try { await sampling.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task SampleLoopAsync(CancellationToken token)
    {
        try
        {
            _sampler.Sample();
            using var timer = new PeriodicTimer(_options.Sampler.Interval, _time);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try { _sampler.Sample(); }
                catch (Exception ex) { _logger.LogWarning(ex, "A process sample failed; the next one will be tried on schedule."); }
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    /// <summary>One scheduler tick: retry the outbox, then run every schedule that is due. Public for tests and for hosts that drive it themselves.</summary>
    public async Task<IReadOnlyList<DiagnosticsReport>> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await ProcessOutboxAsync(now, cancellationToken).ConfigureAwait(false);

        var produced = new List<DiagnosticsReport>();
        foreach (var scheduled in _options.Schedules)
        {
            if (_blockedUntil.TryGetValue(scheduled.Key, out var blocked) && blocked > now) continue;
            var lastRun = _state.GetLastRun(scheduled.Key) ?? _startedAt;
            var due = scheduled.Schedule.NextAfter(lastRun);

            for (var guard = 0; due <= now && guard < 1000; guard++)
            {
                if (now - due > _options.CatchUpGrace)
                {
                    _logger.LogWarning("The {Schedule} report due {Due:u} was missed (the process was not running) and is skipped.", scheduled.Key, due);
                    _state.SetLastRun(scheduled.Key, due);
                    due = scheduled.Schedule.NextAfter(due);
                    continue;
                }

                try
                {
                    var report = await GenerateAndDeliverAsync(scheduled, due, now, cancellationToken).ConfigureAwait(false);
                    produced.Add(report);
                    _state.SetLastRun(scheduled.Key, due);
                    due = scheduled.Schedule.NextAfter(due);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // producing the report itself failed (not delivery): back off instead of retrying every minute
                    _blockedUntil[scheduled.Key] = now + TimeSpan.FromMinutes(15);
                    _status.Failed(now, ReportDeliveryService.Describe(ex));
                    _logger.LogError(ex, "Could not produce the {Schedule} report due {Due:u}; retrying in 15 minutes.", scheduled.Key, due);
                    break;
                }
            }
        }
        return produced;
    }

    /// <summary>Produces a report for any window right now (an on-demand "send me the report"), delivering it to the given sink names or all sinks.</summary>
    public async Task<DiagnosticsReport> GenerateNowAsync(DateTimeOffset? from = null, IEnumerable<ReportFormat>? formats = null,
        IEnumerable<string>? sinkNames = null, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var report = BuildReport(from ?? _startedAt, now, baseline: null, now);
        var files = ReportRenderer.Render(report, formats ?? new[] { ReportFormat.Html, ReportFormat.Json });
        await DeliverAsync(report, "on-demand", files, SelectSinks(sinkNames?.ToList()), now, cancellationToken).ConfigureAwait(false);
        return report;
    }

    private async Task<DiagnosticsReport> GenerateAndDeliverAsync(ScheduledReport scheduled, DateTimeOffset due, DateTimeOffset now, CancellationToken ct)
    {
        var start = scheduled.Schedule.PreviousBefore(due);
        _baselines.TryGetValue(scheduled.Key, out var baseline);
        var current = _telemetry.Snapshot();
        var report = BuildReport(start, due, baseline, now, current);
        _baselines[scheduled.Key] = current;

        var files = ReportRenderer.Render(report, scheduled.Formats);
        await DeliverAsync(report, scheduled.Key, files, SelectSinks(scheduled.SinkNames), now, ct).ConfigureAwait(false);
        return report;
    }

    private DiagnosticsReport BuildReport(DateTimeOffset start, DateTimeOffset end, TelemetrySnapshot? baseline, DateTimeOffset generatedAt, TelemetrySnapshot? current = null)
    {
        current ??= _telemetry.Snapshot();
        var report = _builder.Build(start, end, current, baseline, _sampler.Samples(start, end), _sampler.LiveSet(start, end), generatedAt);
        if (_startedAt > start)
            report = report with
            {
                Findings = report.Findings.Append(new Finding("MS-INFO-002", FindingSeverity.Info, "Partial coverage",
                    $"The process started at {_startedAt.UtcDateTime:u}, after the period began, so only the time since then is covered.")).ToList()
            };
        _status.Generated(generatedAt);
        return report;
    }

    private List<IReportSink> SelectSinks(IReadOnlyList<string>? names) =>
        names == null ? _sinks.ToList() : _sinks.Where(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();

    private async Task DeliverAsync(DiagnosticsReport report, string scheduleName, IReadOnlyList<ReportFile> files, List<IReportSink> sinks,
        DateTimeOffset now, CancellationToken ct)
    {
        var context = new ReportDeliveryContext(report.Application, report.PeriodStart, report.PeriodEnd, scheduleName);
        var results = await _delivery.DeliverAsync(sinks, context, files, ct).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (result.Succeeded) { _status.Delivered(now); continue; }
            _status.Failed(now, $"{result.Sink}: {result.Error}");
            try { _outbox.Enqueue(result.Sink, context, files, result.Error ?? "unknown error", now); }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "The report for sink {Sink} could not be delivered AND could not be saved to the outbox. It is lost.", result.Sink);
            }
        }
        _status.OutboxPending = _outbox.PendingCount;
    }

    private async Task ProcessOutboxAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var (delivered, pending, dead) = await _outbox.ProcessAsync(name => _sinks.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
                _delivery, now, ct).ConfigureAwait(false);
            _status.OutboxPending = pending;
            if (dead > 0) _status.AddDeadLettered(dead);
            if (delivered > 0) _status.Delivered(now);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Processing the report outbox failed; it will be retried on the next tick.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        // leave the mapper as we found it
        if (_config.Observer is CompositeObserver composite) _config.Observer = composite.Without(_telemetry);
        else if (ReferenceEquals(_config.Observer, _telemetry)) _config.Observer = null;
    }
}

/// <summary>Fan-out so reporting can coexist with another observer (for example an APM adapter).</summary>
public sealed class CompositeObserver : IMapperObserver
{
    private readonly IMapperObserver[] _observers;

    public CompositeObserver(params IMapperObserver[] observers) => _observers = observers;

    public bool Contains(IMapperObserver observer) => _observers.Contains(observer);

    public IMapperObserver? Without(IMapperObserver observer)
    {
        var rest = _observers.Where(o => !ReferenceEquals(o, observer)).ToArray();
        return rest.Length switch { 0 => null, 1 => rest[0], _ => new CompositeObserver(rest) };
    }

    public void OnMapped(Type source, Type destination, long ticks) { foreach (var o in _observers) Safe(() => o.OnMapped(source, destination, ticks)); }
    public void OnFailed(Type source, Type destination, long ticks, Exception ex) { foreach (var o in _observers) Safe(() => o.OnFailed(source, destination, ticks, ex)); }
    public void OnCompiled(Type source, Type destination, TimeSpan duration, bool lean) { foreach (var o in _observers) Safe(() => o.OnCompiled(source, destination, duration, lean)); }

    private static void Safe(Action action) { try { action(); } catch { /* one bad observer must not hide the others */ } }
}

public static class ReportingServiceCollectionExtensions
{
    /// <summary>
    /// Registers telemetry, sampling, scheduled reports and delivery. Requires <c>MapperConfiguration</c> in the container
    /// (<c>AddAutoMapping</c> does that). Misconfiguration throws immediately.
    /// </summary>
    public static IServiceCollection AddAutoMappingReporting(this IServiceCollection services, Action<ReportingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new ReportingOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton(new MapperTelemetry(options.Telemetry));
        services.AddSingleton(sp => new ProcessSampler(options.Sampler, () => sp.GetRequiredService<MapperConfiguration>().GetCacheStatistics()));
        services.AddSingleton<ReportingStatus>();
        services.AddSingleton(sp => new ReportScheduler(options, sp.GetRequiredService<MapperConfiguration>(), sp.GetRequiredService<MapperTelemetry>(),
            sp.GetRequiredService<ProcessSampler>(), sp.GetRequiredService<ReportingStatus>(), sp, sp.GetRequiredService<ILogger<ReportScheduler>>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ReportScheduler>());
        return services;
    }
}
