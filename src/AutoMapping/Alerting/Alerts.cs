using AutoMapping.Reporting;

namespace AutoMapping.Alerting;

public enum AlertSeverity { Warning, Critical }

public enum AlertState { Firing, Resolved }

public enum AlertKind { ErrorRate, SlowMapping, SlowCompile, GcPause, CacheGrowth, MemoryLeak, ReportDelivery }

/// <summary>
/// One notification. Alerts carry type names and numbers only: no mapped values and no exception messages.
/// <see cref="Key"/> identifies the condition; the same key is sent as Firing, then (optionally) again as a reminder or escalation,
/// and finally as Resolved.
/// </summary>
public sealed record Alert(string Key, AlertKind Kind, AlertSeverity Severity, AlertState State, string Title, string Message,
    string? Source, string? Destination, double Value, double Threshold, DateTimeOffset At, DateTimeOffset FiringSince, string Application);

/// <summary>A destination for alerts. Implementations must honour the cancellation token and must not put credentials into
/// exception messages (they are logged).</summary>
public interface IAlertSink
{
    string Name { get; }
    Task SendAsync(Alert alert, CancellationToken cancellationToken);
}

/// <summary>Raised at startup when the alert configuration is unusable (fail fast, not at 3 a.m.).</summary>
public sealed class AlertConfigurationException : Exception
{
    public AlertConfigurationException(string message) : base(message) { }
}

/// <summary>Limits that raise an alert. Defaults match the diagnostics report where one exists.</summary>
public sealed class AlertThresholds
{
    /// <summary>Rates and latency are judged only for map pairs with at least this many calls in the window.</summary>
    public long MinCallsForRates { get; set; } = 100;
    public double ErrorRateWarning { get; set; } = 0.01;
    public double ErrorRateCritical { get; set; } = 0.05;
    /// <summary>p95 latency in microseconds. The histogram is accurate to a factor of two (see the diagnostics report).</summary>
    public double SlowP95WarningMicroseconds { get; set; } = 1000;
    public double SlowP95CriticalMicroseconds { get; set; } = 10_000;
    /// <summary>Average compile time of maps compiled during the window.</summary>
    public double SlowCompileMilliseconds { get; set; } = 500;
    public double GcPausePercentWarning { get; set; } = 10;
    public double GcPausePercentCritical { get; set; } = 30;
    /// <summary>Growth of a mapper cache (type maps, mappings, projections) during the window, as a fraction of its starting size.</summary>
    public double CacheGrowthFraction { get; set; } = 0.25;
    /// <summary>Reports failing to deliver this many times in a row.</summary>
    public int DeliveryFailuresWarning { get; set; } = 3;
    public int DeliveryFailuresCritical { get; set; } = 10;
}

public sealed class AlertOptions
{
    private readonly List<Func<IServiceProvider, IAlertSink>> _sinkFactories = new();

    /// <summary>Application name used in alerts. Empty: the name from the reporting options.</summary>
    public string Application { get; set; } = "";

    /// <summary>How often conditions are checked. Default 1 minute.</summary>
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Rates and latency are judged over this recent period. Default 5 minutes.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A condition that stays active is announced again after this long. Default 30 minutes.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A condition must be absent for this many checks in a row before it is Resolved, so a flapping value does not spam.</summary>
    public int ClearEvaluations { get; set; } = 2;

    public bool NotifyOnResolved { get; set; } = true;

    /// <summary>Alerts below this severity are not sent.</summary>
    public AlertSeverity MinSeverity { get; set; } = AlertSeverity.Warning;

    /// <summary>Upper bound on conditions tracked at once; further ones are counted and logged, never stored. Bounds memory.</summary>
    public int MaxActiveAlerts { get; set; } = 500;

    /// <summary>Period used for the memory-leak check (it needs hours of data). Default 6 hours.</summary>
    public TimeSpan LeakWindow { get; set; } = TimeSpan.FromHours(6);

    public AlertThresholds Thresholds { get; } = new();

    /// <summary>Retries and timeouts for sending one alert to one sink.</summary>
    public RetryPolicy Retry { get; } = new() { MaxAttempts = 3 };

    internal IReadOnlyList<Func<IServiceProvider, IAlertSink>> SinkFactories => _sinkFactories;

    public AlertOptions AddSink(IAlertSink sink) => AddSink(_ => sink ?? throw new ArgumentNullException(nameof(sink)));

    public AlertOptions AddSink(Func<IServiceProvider, IAlertSink> factory)
    {
        _sinkFactories.Add(factory ?? throw new ArgumentNullException(nameof(factory)));
        return this;
    }

    /// <summary>Writes alerts to the application log (Warning, Error, and Information for resolved).</summary>
    public AlertOptions AddLog(string name = "log") =>
        AddSink(sp => new LogAlertSink(
            (Microsoft.Extensions.Logging.ILoggerFactory?)sp.GetService(typeof(Microsoft.Extensions.Logging.ILoggerFactory))
                ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, name));

    /// <summary>Runs your code for every alert, for example to call a pager or write to a metrics system.</summary>
    public AlertOptions AddCallback(Func<Alert, CancellationToken, Task> callback, string name = "callback") =>
        AddSink(new CallbackAlertSink(callback, name));

    public AlertOptions AddWebhook(Uri url, string? signingSecret = null, string name = "webhook", HttpClient? client = null) =>
        AddSink(new WebhookAlertSink(url, signingSecret, client, name: name));

    public AlertOptions AddSmtp(Action<SmtpAlertOptions> configure, string name = "email")
    {
        ArgumentNullException.ThrowIfNull(configure);
        var smtp = new SmtpAlertOptions();
        configure(smtp);
        return AddSink(new SmtpAlertSink(smtp, null, name));
    }

    internal void Validate()
    {
        if (_sinkFactories.Count == 0) throw new AlertConfigurationException("Alerts need at least one sink: AddLog(), AddCallback(...), AddWebhook(...) or AddSmtp(...).");
        if (EvaluationInterval < TimeSpan.FromSeconds(5)) throw new AlertConfigurationException("EvaluationInterval must be at least 5 seconds.");
        if (Window < EvaluationInterval) throw new AlertConfigurationException("Window must be at least as long as EvaluationInterval.");
        if (Cooldown < TimeSpan.Zero) throw new AlertConfigurationException("Cooldown cannot be negative.");
        if (ClearEvaluations < 1) throw new AlertConfigurationException("ClearEvaluations must be at least 1.");
        if (MaxActiveAlerts < 1) throw new AlertConfigurationException("MaxActiveAlerts must be at least 1.");
        if (Retry.MaxAttempts < 1) throw new AlertConfigurationException("Retry.MaxAttempts must be at least 1.");
        var t = Thresholds;
        if (t.ErrorRateCritical < t.ErrorRateWarning) throw new AlertConfigurationException("ErrorRateCritical must not be below ErrorRateWarning.");
        if (t.SlowP95CriticalMicroseconds < t.SlowP95WarningMicroseconds) throw new AlertConfigurationException("SlowP95CriticalMicroseconds must not be below SlowP95WarningMicroseconds.");
        if (t.GcPausePercentCritical < t.GcPausePercentWarning) throw new AlertConfigurationException("GcPausePercentCritical must not be below GcPausePercentWarning.");
        if (t.DeliveryFailuresCritical < t.DeliveryFailuresWarning) throw new AlertConfigurationException("DeliveryFailuresCritical must not be below DeliveryFailuresWarning.");
    }
}
