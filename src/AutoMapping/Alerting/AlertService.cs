using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AutoMapping.Reporting;

namespace AutoMapping.Alerting;

/// <summary>
/// Hosted service that checks the telemetry collected by the reporting feature every <see cref="AlertOptions.EvaluationInterval"/>
/// and sends alerts. It only reads: it never touches the mapping path, and it never lets an exception escape (a crashed background
/// service would take the host down). Needs <c>AddAutoMappingReporting</c>, which collects the data and takes the process samples.
/// </summary>
public sealed class AlertService : BackgroundService
{
    private readonly AlertOptions _options;
    private readonly ReportingOptions _reporting;
    private readonly MapperTelemetry _telemetry;
    private readonly ProcessSampler _sampler;
    private readonly ReportingStatus _status;
    private readonly AlertManager _manager;
    private readonly ILogger<AlertService> _logger;
    private readonly TimeProvider _time;
    private readonly List<TelemetrySnapshot> _snapshots = new();

    public AlertService(AlertOptions options, ReportingOptions reporting, MapperTelemetry telemetry, ProcessSampler sampler,
        ReportingStatus status, IServiceProvider services, ILogger<AlertService> logger, TimeProvider? timeProvider = null)
    {
        _options = options;
        _reporting = reporting;
        _telemetry = telemetry;
        _sampler = sampler;
        _status = status;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        var sinks = options.SinkFactories.Select(f => f(services)).ToList();
        var application = string.IsNullOrWhiteSpace(options.Application) ? reporting.Application : options.Application;
        _manager = new AlertManager(options, sinks, application, logger);
    }

    /// <summary>Conditions that are active right now, for your own health endpoint.</summary>
    public IReadOnlyList<Alert> ActiveAlerts => _manager.ActiveAlerts(_time.GetUtcNow());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoMapping alerts started: checking every {Interval}, window {Window}.", _options.EvaluationInterval, _options.Window);
        try
        {
            using var timer = new PeriodicTimer(_options.EvaluationInterval, _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await EvaluateOnceAsync(_time.GetUtcNow(), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "An alert check failed; it will be tried again on the next interval."); }
            }
        }
        catch (OperationCanceledException) { /* stopping */ }
    }

    /// <summary>One check. Public for tests and for hosts that drive it themselves. The first call only records a starting point.</summary>
    public async Task EvaluateOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var current = _telemetry.Snapshot();
        _snapshots.Add(current);
        // keep one snapshot at least a full window old as the baseline; drop anything older than that
        while (_snapshots.Count > 2 && _snapshots[1].TakenAt <= now - _options.Window) _snapshots.RemoveAt(0);
        if (_snapshots.Count < 2) return;

        var baseline = _snapshots[0];
        var conditions = AlertEvaluator.Evaluate(_options.Thresholds, _reporting.Thresholds.Leak, current, baseline,
            _sampler.Samples(now - _options.Window, now), _sampler.LiveSet(now - _options.LeakWindow, now), _status.ConsecutiveFailures);
        await _manager.ProcessAsync(conditions, now, cancellationToken).ConfigureAwait(false);
    }
}

public static class AlertingServiceCollectionExtensions
{
    /// <summary>
    /// Adds alerts for slow mapping, high error rates, slow compiles, GC pressure, cache growth, suspected leaks and failing report
    /// delivery. Needs <c>AddAutoMappingReporting</c> (it collects the data). Nothing changes for mapping or reports.
    /// Misconfiguration throws immediately.
    /// </summary>
    public static IServiceCollection AddAutoMappingAlerts(this IServiceCollection services, Action<AlertOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AlertOptions();
        configure(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton(sp => new AlertService(options,
            sp.GetService<ReportingOptions>() ?? throw MissingReporting(),
            sp.GetService<MapperTelemetry>() ?? throw MissingReporting(),
            sp.GetService<ProcessSampler>() ?? throw MissingReporting(),
            sp.GetService<ReportingStatus>() ?? throw MissingReporting(),
            sp, sp.GetRequiredService<ILogger<AlertService>>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AlertService>());
        return services;
    }

    private static AlertConfigurationException MissingReporting() =>
        new("Alerts need the reporting feature: call services.AddAutoMappingReporting(...) as well.");
}
