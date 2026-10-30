using System.Numerics;

namespace AutoMapping.Reporting;

/// <summary>
/// Fixed-size, lock-free latency histogram: bucket i holds durations in [2^i, 2^(i+1)) microseconds (bucket 0 is below 2 µs).
/// Memory is constant (32 counters) whatever the traffic, which is what keeps long-running telemetry leak-free.
/// Percentiles are therefore upper bounds accurate to a factor of two.
/// </summary>
internal sealed class LatencyHistogram
{
    public const int Buckets = 32;
    private readonly long[] _counts = new long[Buckets];

    public void Record(double microseconds)
    {
        var micros = microseconds < 1 ? 1UL : (ulong)Math.Min(microseconds, (double)(1UL << (Buckets - 1)));
        var index = Math.Min(63 - BitOperations.LeadingZeroCount(micros), Buckets - 1);
        Interlocked.Increment(ref _counts[index]);
    }

    public long[] Snapshot()
    {
        var copy = new long[Buckets];
        for (var i = 0; i < Buckets; i++) copy[i] = Volatile.Read(ref _counts[i]);
        return copy;
    }

    /// <summary>Upper bound (µs) of the bucket containing the given percentile (0..1); 0 when empty.</summary>
    public static double Percentile(IReadOnlyList<long> counts, double percentile)
    {
        long total = 0;
        foreach (var c in counts) total += c;
        if (total == 0) return 0;
        var rank = Math.Max(1, (long)Math.Ceiling(total * percentile));
        long seen = 0;
        for (var i = 0; i < counts.Count; i++)
        {
            seen += counts[i];
            if (seen >= rank) return Math.Pow(2, i + 1);
        }
        return Math.Pow(2, counts.Count);
    }
}
