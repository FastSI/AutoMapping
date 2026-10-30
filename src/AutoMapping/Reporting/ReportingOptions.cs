using System.Net.Http;

namespace AutoMapping.Reporting;

/// <summary>One schedule, the formats to produce and the sinks that receive it (all sinks when none are named).</summary>
public sealed class ScheduledReport
{
    public ScheduledReport(ReportSchedule schedule, IReadOnlyList<ReportFormat> formats, IReadOnlyList<string>? sinkNames)
    {
        Schedule = schedule;
        Formats = formats;
        SinkNames = sinkNames;
    }

    public ReportSchedule Schedule { get; }
    public IReadOnlyList<ReportFormat> Formats { get; }
    public IReadOnlyList<string>? SinkNames { get; }
    /// <summary>Unique key for the state file; two schedules of the same kind need different keys.</summary>
    public string Key { get; set; } = "";
}

public sealed class ReportingOptions
{
    private readonly List<ScheduledReport> _schedules = new();
    private readonly List<Func<IServiceProvider, IReportSink>> _sinkFactories = new();

    public string Application { get; set; } = "application";
    /// <summary>Host names identify infrastructure; they are left out of reports unless you ask for them.</summary>
    public bool IncludeHostName { get; set; }

    public TelemetryOptions Telemetry { get; } = new();
    public SamplerOptions Sampler { get; } = new();
    public ReportThresholds Thresholds { get; } = new();
    public RetryPolicy Retry { get; } = new();

    /// <summary>Where state and the outbox live. Default: the local application-data folder, per application.</summary>
    public string StateDirectory { get; set; } = "";

    /// <summary>If the process was down at the due time, a report is still produced when it comes back within this window; older ones are skipped (and logged).</summary>
    public TimeSpan CatchUpGrace { get; set; } = TimeSpan.FromHours(6);

    public TimeSpan OutboxMaxAge { get; set; } = TimeSpan.FromDays(7);

    public IReadOnlyList<ScheduledReport> Schedules => _schedules;
    internal IReadOnlyList<Func<IServiceProvider, IReportSink>> SinkFactories => _sinkFactories;

    public ReportingOptions Daily(TimeOnly at, TimeZoneInfo? zone = null, ReportFormat[]? formats = null, string[]? sinks = null) =>
        Add(ReportSchedule.Daily(at, zone), formats, sinks);

    public ReportingOptions Weekly(DayOfWeek day, TimeOnly at, TimeZoneInfo? zone = null, ReportFormat[]? formats = null, string[]? sinks = null) =>
        Add(ReportSchedule.Weekly(day, at, zone), formats, sinks);

    public ReportingOptions Monthly(int dayOfMonth, TimeOnly at, TimeZoneInfo? zone = null, ReportFormat[]? formats = null, string[]? sinks = null) =>
        Add(ReportSchedule.Monthly(dayOfMonth, at, zone), formats, sinks);

    private ReportingOptions Add(ReportSchedule schedule, ReportFormat[]? formats, string[]? sinks)
    {
        var report = new ScheduledReport(schedule, formats is { Length: > 0 } ? formats : new[] { ReportFormat.Html, ReportFormat.Json }, sinks);
        report.Key = $"{schedule.Frequency}-{_schedules.Count(s => s.Schedule.Frequency == schedule.Frequency)}";
        _schedules.Add(report);
        return this;
    }

    public ReportingOptions AddSink(IReportSink sink) => AddSink(_ => sink);

    public ReportingOptions AddSink(Func<IServiceProvider, IReportSink> factory)
    {
        _sinkFactories.Add(factory ?? throw new ArgumentNullException(nameof(factory)));
        return this;
    }

    public ReportingOptions AddLocalDirectory(string directory, int? retentionDays = null, string name = "local") =>
        AddSink(new LocalDirectorySink(directory, retentionDays, name));

    public ReportingOptions AddWebhook(Uri url, string? signingSecret = null, string name = "webhook", HttpClient? client = null) =>
        AddSink(new WebhookSink(url, signingSecret, client, name: name));

    internal void Validate()
    {
        if (_schedules.Count == 0) throw new ReportingConfigurationException("Reporting needs at least one schedule: call Daily(...), Weekly(...) or Monthly(...).");
        if (_sinkFactories.Count == 0) throw new ReportingConfigurationException("Reporting needs at least one sink: AddLocalDirectory(...), AddWebhook(...) or AddSftp(...).");
        if (Retry.MaxAttempts < 1) throw new ReportingConfigurationException("Retry.MaxAttempts must be at least 1.");
        if (Sampler.Interval < TimeSpan.FromSeconds(1)) throw new ReportingConfigurationException("Sampler.Interval must be at least one second.");
        if (CatchUpGrace < TimeSpan.Zero) throw new ReportingConfigurationException("CatchUpGrace cannot be negative.");
    }

    internal string ResolveStateDirectory()
    {
        if (!string.IsNullOrWhiteSpace(StateDirectory)) return Path.GetFullPath(StateDirectory);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        return Path.Combine(root, "AutoMapping", "reporting", ReportRenderer.Safe(Application));
    }
}

/// <summary>Health and progress of the reporting pipeline, safe to read from a health-check endpoint.</summary>
public sealed class ReportingStatus
{
    private readonly object _lock = new();
    private string? _lastError;

    public DateTimeOffset? LastReportGenerated { get; private set; }
    public DateTimeOffset? LastSuccessfulDelivery { get; private set; }
    public DateTimeOffset? LastFailure { get; private set; }
    private int _consecutiveFailures, _deadLettered;

    public int ConsecutiveFailures { get { lock (_lock) return _consecutiveFailures; } }
    public int OutboxPending { get; internal set; }
    public int DeadLettered { get { lock (_lock) return _deadLettered; } }
    public string? LastError { get { lock (_lock) return _lastError; } }
    public bool Healthy => ConsecutiveFailures == 0;

    internal void Generated(DateTimeOffset at) { lock (_lock) LastReportGenerated = at; }

    internal void Delivered(DateTimeOffset at) { lock (_lock) { LastSuccessfulDelivery = at; _consecutiveFailures = 0; _lastError = null; } }

    internal void Failed(DateTimeOffset at, string error) { lock (_lock) { LastFailure = at; _consecutiveFailures++; _lastError = error; } }

    internal void AddDeadLettered(int count) { lock (_lock) _deadLettered += count; }
}
