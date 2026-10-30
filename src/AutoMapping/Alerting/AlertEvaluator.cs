using System.Globalization;
using AutoMapping.Reporting;

namespace AutoMapping.Alerting;

/// <summary>A limit that is currently crossed. The manager turns these into <see cref="Alert"/>s.</summary>
public sealed record AlertCondition(string Key, AlertKind Kind, AlertSeverity Severity, string Title, string Message,
    string? Source, string? Destination, double Value, double Threshold);

/// <summary>
/// Pure function from telemetry to conditions: no clocks, no I/O, no state. The same inputs always give the same output,
/// which is what makes it easy to test.
/// </summary>
public static class AlertEvaluator
{
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Pct(double v) => (v * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    public static IReadOnlyList<AlertCondition> Evaluate(AlertThresholds t, LeakDetectionOptions leakOptions, TelemetrySnapshot current,
        TelemetrySnapshot? baseline, IReadOnlyList<ProcessSample> samples, IReadOnlyList<LiveSetObservation> liveSet, int consecutiveDeliveryFailures)
    {
        var result = new List<AlertCondition>();
        var before = baseline?.Pairs.ToDictionary(p => (p.Source, p.Destination)) ?? new Dictionary<(string, string), PairSnapshot>();

        foreach (var p in current.Pairs)
        {
            before.TryGetValue((p.Source, p.Destination), out var b);
            var calls = p.Calls - (b?.Calls ?? 0);
            if (calls < t.MinCallsForRates || calls <= 0) continue;
            var errors = p.Errors - (b?.Errors ?? 0);
            var pair = $"{p.Source} -> {p.Destination}";

            var rate = errors / (double)calls;
            if (rate >= t.ErrorRateWarning)
            {
                var critical = rate >= t.ErrorRateCritical;
                result.Add(new AlertCondition($"error-rate|{pair}", AlertKind.ErrorRate, critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                    $"High error rate: {pair}",
                    $"{Pct(rate)} of {calls} mappings failed in the last window ({errors} errors). Limit: {Pct(critical ? t.ErrorRateCritical : t.ErrorRateWarning)}.",
                    p.Source, p.Destination, rate, critical ? t.ErrorRateCritical : t.ErrorRateWarning));
            }

            var histogram = p.Histogram.Select((c, i) => Math.Max(0, c - (b?.Histogram[i] ?? 0))).ToArray();
            var p95 = LatencyHistogram.Percentile(histogram, 0.95);
            if (p95 >= t.SlowP95WarningMicroseconds)
            {
                var critical = p95 >= t.SlowP95CriticalMicroseconds;
                var limit = critical ? t.SlowP95CriticalMicroseconds : t.SlowP95WarningMicroseconds;
                result.Add(new AlertCondition($"slow|{pair}", AlertKind.SlowMapping, critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                    $"Slow mapping: {pair}",
                    $"p95 latency is up to {F(p95)} µs over {calls} mappings in the last window. Limit: {F(limit)} µs.",
                    p.Source, p.Destination, p95, limit));
            }
        }

        var beforeCompiles = baseline?.Compiles.ToDictionary(c => (c.Source, c.Destination)) ?? new Dictionary<(string, string), CompileSnapshot>();
        foreach (var c in current.Compiles)
        {
            beforeCompiles.TryGetValue((c.Source, c.Destination), out var b);
            var count = c.Compiles - (b?.Compiles ?? 0);
            if (count <= 0) continue;
            var average = (c.TotalMilliseconds - (b?.TotalMilliseconds ?? 0)) / count;
            if (average < t.SlowCompileMilliseconds) continue;
            result.Add(new AlertCondition($"compile|{c.Source} -> {c.Destination}", AlertKind.SlowCompile, AlertSeverity.Warning,
                $"Slow map compile: {c.Source} -> {c.Destination}",
                $"Compiling took {F(average)} ms on average ({count} compile(s) in the last window). Limit: {F(t.SlowCompileMilliseconds)} ms.",
                c.Source, c.Destination, average, t.SlowCompileMilliseconds));
        }

        if (samples.Count > 0)
        {
            var gc = samples.Max(s => s.GcPauseTimePercent);
            if (gc >= t.GcPausePercentWarning)
            {
                var critical = gc >= t.GcPausePercentCritical;
                var limit = critical ? t.GcPausePercentCritical : t.GcPausePercentWarning;
                result.Add(new AlertCondition("gc-pause", AlertKind.GcPause, critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                    "High GC pause time",
                    $"The process spent up to {F(gc)}% of its time paused for garbage collection in the last window. Limit: {F(limit)}%.",
                    null, null, gc, limit));
            }
        }

        if (samples.Count >= 2)
        {
            var first = samples[0].Caches;
            var last = samples[^1].Caches;
            var grown = new List<string>();
            double worst = 0;
            void Check(string name, int a, int z)
            {
                var growth = (z - a) / (double)Math.Max(a, 1);
                if (growth < t.CacheGrowthFraction) return;
                grown.Add($"{name} {a} -> {z}");
                worst = Math.Max(worst, growth);
            }
            Check("type maps", first.TypeMaps, last.TypeMaps);
            Check("mappings", first.Mappings, last.Mappings);
            Check("projections", first.Projections, last.Projections);
            if (grown.Count > 0)
                result.Add(new AlertCondition("cache-growth", AlertKind.CacheGrowth, AlertSeverity.Warning, "Mapper caches are growing",
                    $"Growth over the last window: {string.Join(", ", grown)}. Steady growth without new maps usually means runtime types are being created without limit.",
                    null, null, worst, t.CacheGrowthFraction));
        }

        var leak = LeakAnalyzer.Analyze(liveSet, leakOptions);
        if (leak.Verdict == LeakVerdict.Suspected)
            result.Add(new AlertCondition("memory-leak", AlertKind.MemoryLeak, AlertSeverity.Warning, "Possible memory leak",
                $"The live set after full GCs is rising: {F(leak.SlopeBytesPerHour / 1024 / 1024)} MiB/hour, {Pct(leak.RelativeGrowth)} growth over {leak.Observations} observations. " +
                "This is a heuristic: confirm with a memory dump.",
                null, null, leak.SlopeBytesPerHour, leakOptions.MinSlopeBytesPerHour));

        if (consecutiveDeliveryFailures >= t.DeliveryFailuresWarning)
        {
            var critical = consecutiveDeliveryFailures >= t.DeliveryFailuresCritical;
            var limit = critical ? t.DeliveryFailuresCritical : t.DeliveryFailuresWarning;
            result.Add(new AlertCondition("report-delivery", AlertKind.ReportDelivery, critical ? AlertSeverity.Critical : AlertSeverity.Warning,
                "Diagnostics reports are failing to deliver",
                $"{consecutiveDeliveryFailures} deliveries in a row have failed. Limit: {limit}. Check the report sinks and the outbox.",
                null, null, consecutiveDeliveryFailures, limit));
        }

        return result;
    }
}
