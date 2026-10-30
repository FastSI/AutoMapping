using System.Reflection;
using System.Runtime.InteropServices;

namespace AutoMapping.Reporting;

/// <summary>Turns two telemetry snapshots and the process samples between them into a <see cref="DiagnosticsReport"/> with findings.</summary>
public sealed class ReportBuilder
{
    private readonly string _application;
    private readonly ReportThresholds _thresholds;
    private readonly bool _includeHostName;

    public ReportBuilder(string application, ReportThresholds? thresholds = null, bool includeHostName = false)
    {
        _application = string.IsNullOrWhiteSpace(application) ? "application" : application;
        _thresholds = thresholds ?? new ReportThresholds();
        _includeHostName = includeHostName;
    }

    public DiagnosticsReport Build(DateTimeOffset start, DateTimeOffset end, TelemetrySnapshot current, TelemetrySnapshot? previous,
        IReadOnlyList<ProcessSample> samples, IReadOnlyList<LiveSetObservation> liveSet, DateTimeOffset generatedAt)
    {
        var seconds = Math.Max(1, (end - start).TotalSeconds);
        var previousPairs = previous?.Pairs.ToDictionary(p => (p.Source, p.Destination)) ?? new Dictionary<(string, string), PairSnapshot>();

        var pairs = new List<PairReport>();
        foreach (var p in current.Pairs)
        {
            previousPairs.TryGetValue((p.Source, p.Destination), out var before);
            var calls = p.Calls - (before?.Calls ?? 0);
            if (calls <= 0) continue;
            var errors = p.Errors - (before?.Errors ?? 0);
            var totalMs = p.TotalMilliseconds - (before?.TotalMilliseconds ?? 0);
            var histogram = p.Histogram.Select((c, i) => Math.Max(0, c - (before?.Histogram[i] ?? 0))).ToArray();
            pairs.Add(new PairReport(p.Source, p.Destination, calls, errors, errors / (double)calls, totalMs * 1000 / calls,
                LatencyHistogram.Percentile(histogram, 0.50), LatencyHistogram.Percentile(histogram, 0.95), LatencyHistogram.Percentile(histogram, 0.99), p.MaxMilliseconds));
        }

        var previousExceptions = previous?.Exceptions.ToDictionary(e => e.Key) ?? new Dictionary<string, ExceptionSnapshot>();
        var exceptions = current.Exceptions
            .Select(e => (e, delta: e.Count - (previousExceptions.TryGetValue(e.Key, out var b) ? b.Count : 0)))
            .Where(x => x.delta > 0)
            .OrderByDescending(x => x.delta)
            .Select(x => new ExceptionReport(x.e.ExceptionType, x.e.Source, x.e.Destination, x.delta, x.e.FirstSeen, x.e.LastSeen, x.e.Message, x.e.TopFrames))
            .ToList();

        var previousCompiles = previous?.Compiles.ToDictionary(c => (c.Source, c.Destination)) ?? new Dictionary<(string, string), CompileSnapshot>();
        var compiles = current.Compiles
            .Select(c =>
            {
                previousCompiles.TryGetValue((c.Source, c.Destination), out var b);
                return new CompileReport(c.Source, c.Destination, c.Compiles - (b?.Compiles ?? 0), c.TotalMilliseconds - (b?.TotalMilliseconds ?? 0), c.MaxMilliseconds);
            })
            .Where(c => c.Compiles > 0)
            .OrderByDescending(c => c.TotalMilliseconds)
            .ToList();

        var totalCalls = pairs.Sum(p => p.Calls);
        var totalErrors = pairs.Sum(p => p.Errors);
        var totalMs2 = pairs.Sum(p => p.AverageMicroseconds * p.Calls) / 1000.0;
        var summary = new ReportSummary(totalCalls, totalErrors, totalCalls == 0 ? 0 : totalErrors / (double)totalCalls, totalCalls / seconds,
            totalCalls == 0 ? 0 : totalMs2 / totalCalls, pairs.Count, exceptions.Count,
            current.DroppedExceptionGroups - (previous?.DroppedExceptionGroups ?? 0), current.OverflowPairCalls - (previous?.OverflowPairCalls ?? 0));

        var memory = BuildMemory(samples, liveSet);
        var report = new DiagnosticsReport(_application, start, end, generatedAt, Environment(), summary,
            pairs.OrderByDescending(p => p.Calls).Take(_thresholds.TopN).ToList(),
            exceptions.Take(_thresholds.TopN).ToList(), compiles.Take(_thresholds.TopN).ToList(), memory, Array.Empty<Finding>());
        // findings look at the complete lists, the report keeps the top N
        return report with { Findings = Evaluate(pairs, exceptions, compiles, summary, memory) };
    }

    private MemoryReport BuildMemory(IReadOnlyList<ProcessSample> samples, IReadOnlyList<LiveSetObservation> liveSet)
    {
        var leak = LeakAnalyzer.Analyze(liveSet, _thresholds.Leak);
        if (samples.Count == 0)
            return new MemoryReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, leak);

        var first = samples[0];
        var last = samples[^1];
        return new MemoryReport(samples.Count,
            samples.Min(s => s.WorkingSetBytes), samples.Max(s => s.WorkingSetBytes), (long)samples.Average(s => s.WorkingSetBytes),
            samples.Min(s => s.ManagedHeapBytes), samples.Max(s => s.ManagedHeapBytes), Math.Max(0, last.AllocatedBytesTotal - first.AllocatedBytesTotal),
            Math.Max(0, last.Gen0 - first.Gen0), Math.Max(0, last.Gen1 - first.Gen1), Math.Max(0, last.Gen2 - first.Gen2),
            samples.Average(s => s.GcPauseTimePercent), samples.Max(s => s.GcPauseTimePercent), samples.Max(s => s.ThreadPoolThreads),
            Math.Max(0, last.CpuSeconds - first.CpuSeconds), first.Caches, last.Caches, leak);
    }

    private ReportEnvironment Environment()
    {
        var version = typeof(MapperConfiguration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(MapperConfiguration).Assembly.GetName().Version?.ToString() ?? "unknown";
        return new ReportEnvironment(version.Split('+')[0], RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            System.Environment.ProcessorCount, _includeHostName ? System.Environment.MachineName : null);
    }

    private List<Finding> Evaluate(List<PairReport> pairs, List<ExceptionReport> exceptions, List<CompileReport> compiles, ReportSummary summary, MemoryReport memory)
    {
        var t = _thresholds;
        var findings = new List<Finding>();

        if (summary.TotalCalls == 0)
            findings.Add(new("MS-INFO-001", FindingSeverity.Info, "No mapping activity", "No mapper calls were recorded in this period. Check that the observer is attached and the application was running."));

        if (summary.TotalCalls >= t.MinCallsForRates && summary.ErrorRate >= t.ErrorRateWarning)
            findings.Add(new("MS-ERR-001", summary.ErrorRate >= t.ErrorRateCritical ? FindingSeverity.Critical : FindingSeverity.Warning,
                "High mapping error rate", $"{summary.TotalErrors:N0} of {summary.TotalCalls:N0} calls failed ({summary.ErrorRate:P2})."));

        foreach (var p in pairs.Where(p => p.Calls >= t.MinCallsForRates && p.ErrorRate >= t.ErrorRateWarning).OrderByDescending(p => p.ErrorRate).Take(5))
            findings.Add(new("MS-ERR-002", p.ErrorRate >= t.ErrorRateCritical ? FindingSeverity.Critical : FindingSeverity.Warning,
                $"Failing map {p.Source} -> {p.Destination}", $"{p.Errors:N0} of {p.Calls:N0} calls failed ({p.ErrorRate:P2})."));

        if (exceptions.Count > 0)
        {
            var top = exceptions[0];
            findings.Add(new("MS-ERR-003", FindingSeverity.Warning, $"{exceptions.Count} distinct exception group(s)",
                $"Most frequent: {top.ExceptionType} in {top.Source} -> {top.Destination} ({top.Count:N0}x)."));
        }

        foreach (var p in pairs.Where(p => p.Calls >= t.MinCallsForRates && p.P95Microseconds > t.SlowP95Microseconds).OrderByDescending(p => p.P95Microseconds).Take(5))
            findings.Add(new("MS-PERF-001", FindingSeverity.Warning, $"Slow map {p.Source} -> {p.Destination}",
                $"p95 is below {p.P95Microseconds / 1000:F2} ms and p99 below {p.P99Microseconds / 1000:F2} ms over {p.Calls:N0} calls. Look for resolvers doing I/O or heavy conversions."));

        foreach (var c in compiles.Where(c => c.MaxMilliseconds > t.SlowCompileMilliseconds).Take(5))
            findings.Add(new("MS-PERF-002", FindingSeverity.Info, $"Slow first use of {c.Source} -> {c.Destination}",
                $"Compiling took up to {c.MaxMilliseconds:F0} ms. Call CompileMappings() at startup to pay this before traffic arrives."));

        if (memory.Leak.Verdict == LeakVerdict.Suspected)
            findings.Add(new("MS-MEM-001", FindingSeverity.Critical, "Possible memory leak", memory.Leak.Explanation + " This is a statistical indicator: confirm with a memory dump."));

        if (memory.GcPauseMaxPercent >= t.GcPausePercentWarning)
            findings.Add(new("MS-MEM-002", FindingSeverity.Warning, "High GC pause time", $"GC pause time reached {memory.GcPauseMaxPercent:F1}% (average {memory.GcPauseAveragePercent:F1}%)."));

        if (memory.CachesAtStart is { } a && memory.CachesAtEnd is { } b)
        {
            foreach (var (name, from, to) in new[] { ("type maps", a.TypeMaps, b.TypeMaps), ("compiled mappings", a.Mappings, b.Mappings), ("projections", a.Projections, b.Projections) })
                if (to > from && to - from >= 10 && (to - from) / (double)Math.Max(1, from) >= t.CacheGrowthFraction)
                    findings.Add(new("MS-MEM-003", FindingSeverity.Warning, $"Mapper cache growth: {name}", $"{name} grew from {from} to {to} during the period. A steady increase means maps are being created for ever-new runtime types (for example CreateMissingTypeMaps with dynamic types)."));
        }

        if (summary.DroppedExceptionGroups > 0 || summary.UntrackedPairCalls > 0)
            findings.Add(new("MS-TEL-001", FindingSeverity.Info, "Telemetry limits reached",
                $"{summary.UntrackedPairCalls:N0} calls were counted under \"(other)\" and {summary.DroppedExceptionGroups:N0} exception occurrences were not grouped because the configured caps were reached."));

        return findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Code, StringComparer.Ordinal).ToList();
    }
}
