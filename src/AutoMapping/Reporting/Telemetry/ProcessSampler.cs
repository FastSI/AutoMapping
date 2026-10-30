using System.Diagnostics;

namespace AutoMapping.Reporting;

public sealed record ProcessSample(DateTimeOffset At, long WorkingSetBytes, long ManagedHeapBytes, long AllocatedBytesTotal,
    int Gen0, int Gen1, int Gen2, double GcPauseTimePercent, int ThreadPoolThreads, double CpuSeconds, MapperCacheStatistics Caches);

/// <summary>A live-set measurement taken right after a generation-2 GC: the series the leak check looks at.</summary>
public readonly record struct LiveSetObservation(DateTimeOffset At, long LiveBytes);

public sealed class SamplerOptions
{
    /// <summary>How often a sample is taken. Default 5 minutes.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Recent, fine-grained samples kept (default 576 = two days at 5 minutes).</summary>
    public int FineCapacity { get; set; } = 576;
    /// <summary>One sample per hour kept for long reports (default 800 = 33 days).</summary>
    public int CoarseCapacity { get; set; } = 800;
    public int LiveSetCapacity { get; set; } = 1000;
}

/// <summary>Takes periodic process/GC/cache samples into fixed-size ring buffers (memory use is constant).</summary>
public sealed class ProcessSampler
{
    private readonly SamplerOptions _options;
    private readonly Func<MapperCacheStatistics> _caches;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Queue<ProcessSample> _fine = new();
    private readonly Queue<ProcessSample> _coarse = new();
    private readonly Queue<LiveSetObservation> _liveSet = new();
    private long _lastGcIndex = -1;
    private DateTimeOffset _lastCoarse = DateTimeOffset.MinValue;

    public ProcessSampler(SamplerOptions options, Func<MapperCacheStatistics> caches, TimeProvider? timeProvider = null)
    {
        _options = options;
        _caches = caches;
        _time = timeProvider ?? TimeProvider.System;
    }

    public ProcessSample Sample()
    {
        var now = _time.GetUtcNow();
        var gcInfo = GC.GetGCMemoryInfo(GCKind.Any);
        double cpu;
        using (var process = Process.GetCurrentProcess()) cpu = process.TotalProcessorTime.TotalSeconds;

        var sample = new ProcessSample(now, Environment.WorkingSet, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(false),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), gcInfo.PauseTimePercentage, ThreadPool.ThreadCount, cpu, _caches());

        lock (_lock)
        {
            Enqueue(_fine, sample, _options.FineCapacity);
            if (now - _lastCoarse >= TimeSpan.FromHours(1))
            {
                Enqueue(_coarse, sample, _options.CoarseCapacity);
                _lastCoarse = now;
            }
            // a new generation-2 GC: its promoted bytes are the objects that are still alive, i.e. retained memory
            var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
            var observed = gcInfo.Generation == 2 ? gcInfo : full;
            if (observed.Generation == 2 && observed.Index != _lastGcIndex && observed.PromotedBytes > 0)
            {
                _lastGcIndex = observed.Index;
                Enqueue(_liveSet, new LiveSetObservation(now, observed.PromotedBytes), _options.LiveSetCapacity);
            }
        }
        return sample;
    }

    /// <summary>Records a live-set observation directly (used by tests and by hosts that run their own full GC).</summary>
    public void AddLiveSetObservation(LiveSetObservation observation)
    {
        lock (_lock) Enqueue(_liveSet, observation, _options.LiveSetCapacity);
    }

    private static void Enqueue<T>(Queue<T> queue, T item, int capacity)
    {
        queue.Enqueue(item);
        while (queue.Count > Math.Max(1, capacity)) queue.Dequeue();
    }

    /// <summary>Samples inside the window, from the fine buffer when it covers the window, else from the hourly one.</summary>
    public IReadOnlyList<ProcessSample> Samples(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            var source = _fine.Count > 0 && _fine.Peek().At <= from ? _fine : (_coarse.Count > 0 ? _coarse : _fine);
            return source.Where(s => s.At >= from && s.At <= to).ToList();
        }
    }

    public IReadOnlyList<LiveSetObservation> LiveSet(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock) return _liveSet.Where(o => o.At >= from && o.At <= to).ToList();
    }
}

public sealed class LeakDetectionOptions
{
    /// <summary>Minimum live-set observations before a verdict is given.</summary>
    public int MinObservations { get; set; } = 6;
    public TimeSpan MinSpan { get; set; } = TimeSpan.FromHours(1);
    /// <summary>Growth per hour (bytes) above which a rising trend counts. Default 1 MiB/h.</summary>
    public double MinSlopeBytesPerHour { get; set; } = 1024 * 1024;
    /// <summary>How well a straight line must fit (0..1) before the trend is believed. Default 0.8.</summary>
    public double MinRSquared { get; set; } = 0.8;
    /// <summary>Total growth over the window, as a fraction of the starting size. Default 20 %.</summary>
    public double MinRelativeGrowth { get; set; } = 0.2;
}

public enum LeakVerdict { InsufficientData, Stable, Suspected }

public sealed record LeakAnalysis(LeakVerdict Verdict, int Observations, double SlopeBytesPerHour, double RSquared, double RelativeGrowth,
    long FirstBytes, long LastBytes, string Explanation);

/// <summary>
/// A heuristic, not proof: a steadily rising live set across many full GCs (a straight-line fit with high R² and a material relative
/// growth) is the classic signature of a leak. Verify with a memory dump before acting on a "Suspected" verdict.
/// </summary>
public static class LeakAnalyzer
{
    public static LeakAnalysis Analyze(IReadOnlyList<LiveSetObservation> observations, LeakDetectionOptions? options = null)
    {
        options ??= new LeakDetectionOptions();
        if (observations.Count < options.MinObservations)
            return new LeakAnalysis(LeakVerdict.InsufficientData, observations.Count, 0, 0, 0, 0, 0,
                $"Only {observations.Count} full-GC observations in the window; at least {options.MinObservations} are needed.");

        var ordered = observations.OrderBy(o => o.At).ToList();
        var span = ordered[^1].At - ordered[0].At;
        if (span < options.MinSpan)
            return new LeakAnalysis(LeakVerdict.InsufficientData, ordered.Count, 0, 0, 0, ordered[0].LiveBytes, ordered[^1].LiveBytes,
                $"The observations cover {span.TotalMinutes:F0} minutes; at least {options.MinSpan.TotalMinutes:F0} are needed.");

        var t0 = ordered[0].At;
        var xs = ordered.Select(o => (o.At - t0).TotalHours).ToArray();
        var ys = ordered.Select(o => (double)o.LiveBytes).ToArray();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var sxx = xs.Sum(x => (x - meanX) * (x - meanX));
        var sxy = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum();
        var syy = ys.Sum(y => (y - meanY) * (y - meanY));
        var slope = sxx == 0 ? 0 : sxy / sxx;
        var r2 = sxx == 0 || syy == 0 ? 0 : sxy * sxy / (sxx * syy);

        var first = ordered[0].LiveBytes;
        var last = ordered[^1].LiveBytes;
        var relative = first <= 0 ? 0 : (last - first) / (double)first;

        var suspected = slope >= options.MinSlopeBytesPerHour && r2 >= options.MinRSquared && relative >= options.MinRelativeGrowth;
        var text = suspected
            ? $"The live set grew from {Bytes(first)} to {Bytes(last)} ({relative:P0}) at about {Bytes((long)slope)}/hour with a steady trend (R²={r2:F2}) across {ordered.Count} full GCs."
            : $"No sustained growth: {Bytes(first)} to {Bytes(last)} ({relative:P0}), slope {Bytes((long)slope)}/hour, R²={r2:F2}.";
        return new LeakAnalysis(suspected ? LeakVerdict.Suspected : LeakVerdict.Stable, ordered.Count, slope, r2, relative, first, last, text);
    }

    internal static string Bytes(long value)
    {
        var abs = Math.Abs((double)value);
        return abs >= 1L << 30 ? $"{value / (double)(1L << 30):F2} GiB" : abs >= 1L << 20 ? $"{value / (double)(1L << 20):F1} MiB" : abs >= 1L << 10 ? $"{value / 1024.0:F1} KiB" : $"{value} B";
    }
}
