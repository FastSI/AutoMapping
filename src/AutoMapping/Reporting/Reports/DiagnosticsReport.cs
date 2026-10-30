namespace AutoMapping.Reporting;

public enum FindingSeverity { Info, Warning, Critical }

/// <summary>A rule result. <see cref="Code"/> is stable so alerts and dashboards can key on it.</summary>
public sealed record Finding(string Code, FindingSeverity Severity, string Title, string Detail);

public sealed record ReportSummary(long TotalCalls, long TotalErrors, double ErrorRate, double CallsPerSecond, double AverageMilliseconds,
    int MapsObserved, int ExceptionGroups, long DroppedExceptionGroups, long UntrackedPairCalls);

public sealed record PairReport(string Source, string Destination, long Calls, long Errors, double ErrorRate, double AverageMicroseconds,
    double P50Microseconds, double P95Microseconds, double P99Microseconds, double MaxMillisecondsSinceStart);

public sealed record ExceptionReport(string ExceptionType, string Source, string Destination, long Count, DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen, string? Message, IReadOnlyList<string> TopFrames);

public sealed record CompileReport(string Source, string Destination, int Compiles, double TotalMilliseconds, double MaxMilliseconds);

public sealed record MemoryReport(int Samples, long WorkingSetMinBytes, long WorkingSetMaxBytes, long WorkingSetAverageBytes,
    long ManagedHeapMinBytes, long ManagedHeapMaxBytes, long AllocatedBytesInPeriod, int Gen0Collections, int Gen1Collections, int Gen2Collections,
    double GcPauseAveragePercent, double GcPauseMaxPercent, int ThreadPoolThreadsMax, double CpuSecondsInPeriod,
    MapperCacheStatistics? CachesAtStart, MapperCacheStatistics? CachesAtEnd, LeakAnalysis Leak);

public sealed record ReportEnvironment(string LibraryVersion, string Runtime, string OperatingSystem, int ProcessorCount, string? HostName);

public sealed record DiagnosticsReport(string Application, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, DateTimeOffset GeneratedAt,
    ReportEnvironment Environment, ReportSummary Summary, IReadOnlyList<PairReport> Performance, IReadOnlyList<ExceptionReport> Exceptions,
    IReadOnlyList<CompileReport> Compiles, MemoryReport Memory, IReadOnlyList<Finding> Findings)
{
    public FindingSeverity WorstSeverity => Findings.Count == 0 ? FindingSeverity.Info : Findings.Max(f => f.Severity);
}

public sealed class ReportThresholds
{
    public long MinCallsForRates { get; set; } = 100;
    public double ErrorRateWarning { get; set; } = 0.01;
    public double ErrorRateCritical { get; set; } = 0.05;
    /// <summary>p95 above this (microseconds) raises a warning. Default 1 ms: mapping is normally microseconds.</summary>
    public double SlowP95Microseconds { get; set; } = 1000;
    public double SlowCompileMilliseconds { get; set; } = 500;
    public double GcPausePercentWarning { get; set; } = 10;
    /// <summary>Cache growth (any of the maps/mappings/projections) during the period above this fraction raises a warning.</summary>
    public double CacheGrowthFraction { get; set; } = 0.25;
    public int TopN { get; set; } = 25;
    public LeakDetectionOptions Leak { get; set; } = new();
}
