using System.Collections.Concurrent;
using System.Diagnostics;

namespace AutoMapping.Reporting;

public sealed class TelemetryOptions
{
    /// <summary>Distinct (source, destination) pairs tracked individually; further pairs are folded into "(other)". Bounds memory.</summary>
    public int MaxTrackedPairs { get; set; } = 500;
    /// <summary>Distinct exception groups kept; further groups only increment a dropped counter. Bounds memory.</summary>
    public int MaxExceptionGroups { get; set; } = 200;
    /// <summary>Exception messages can contain customer data. Off by default; AutoMapping's own exceptions (type names only) are always kept.</summary>
    public bool CaptureExceptionMessages { get; set; }
    /// <summary>Applied to every captured exception message (for example to mask e-mail addresses or ids) before it is stored.
    /// If it throws, the message is dropped. Only used when <see cref="CaptureExceptionMessages"/> is on.</summary>
    public Func<string, string>? MessageRedactor { get; set; }
    public int MaxMessageLength { get; set; } = 300;
    /// <summary>Stack frames used to tell two exception sites apart.</summary>
    public int StackFramesInGroupKey { get; set; } = 4;
}

/// <summary>Cumulative per-pair counters. A report is the difference of two snapshots.</summary>
public sealed record PairSnapshot(string Source, string Destination, long Calls, long Errors, double TotalMilliseconds, double MaxMilliseconds, IReadOnlyList<long> Histogram);

public sealed record ExceptionSnapshot(string Key, string ExceptionType, string Source, string Destination, long Count,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string? Message, IReadOnlyList<string> TopFrames);

public sealed record CompileSnapshot(string Source, string Destination, int Compiles, double TotalMilliseconds, double MaxMilliseconds);

public sealed record TelemetrySnapshot(DateTimeOffset TakenAt, IReadOnlyList<PairSnapshot> Pairs, IReadOnlyList<ExceptionSnapshot> Exceptions,
    IReadOnlyList<CompileSnapshot> Compiles, long DroppedExceptionGroups, long OverflowPairCalls);

/// <summary>
/// Collects call counts, latency histograms, exception groups and compile times from a <see cref="MapperConfiguration"/>.
/// Thread-safe, allocation-light on the hot path and bounded in memory.
/// </summary>
public sealed class MapperTelemetry : IMapperObserver
{
    private sealed class PairStats
    {
        public long Calls, Errors, TotalTicks, MaxTicks;
        public readonly LatencyHistogram Histogram = new();
    }

    private sealed class ExceptionGroup
    {
        public string Key = "", Type = "", Source = "", Destination = "";
        public long Count;
        public long FirstTicks, LastTicks;
        public string? Message;
        public string[] Frames = Array.Empty<string>();
    }

    private sealed class CompileStats { public int Compiles; public long TotalTicks, MaxTicks; }

    private readonly TelemetryOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(Type, Type), PairStats> _pairs = new();
    private readonly PairStats _overflow = new();
    private readonly ConcurrentDictionary<string, ExceptionGroup> _exceptions = new();
    private readonly ConcurrentDictionary<(Type, Type), CompileStats> _compiles = new();
    private long _droppedExceptionGroups;

    public MapperTelemetry(TelemetryOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new TelemetryOptions();
        _time = timeProvider ?? TimeProvider.System;
    }

    public void OnMapped(Type source, Type destination, long elapsedTimestampTicks) => Stats(source, destination).Record(elapsedTimestampTicks, failed: false);

    public void OnFailed(Type source, Type destination, long elapsedTimestampTicks, Exception exception)
    {
        Stats(source, destination).Record(elapsedTimestampTicks, failed: true);
        RecordException(source, destination, exception);
    }

    public void OnCompiled(Type source, Type destination, TimeSpan duration, bool contextFree)
    {
        if (_compiles.Count >= _options.MaxTrackedPairs && !_compiles.ContainsKey((source, destination))) return;
        var stats = _compiles.GetOrAdd((source, destination), static _ => new CompileStats());
        lock (stats)
        {
            stats.Compiles++;
            stats.TotalTicks += duration.Ticks;
            stats.MaxTicks = Math.Max(stats.MaxTicks, duration.Ticks);
        }
    }

    // The same pair is usually mapped many times in a row on one thread: remember the last lookup per thread (reference checks only).
    [ThreadStatic] private static MapperTelemetry? _lastOwner;
    [ThreadStatic] private static Type? _lastSource;
    [ThreadStatic] private static Type? _lastDestination;
    [ThreadStatic] private static PairStats? _lastStats;

    private StatsHandle Stats(Type source, Type destination)
    {
        if (ReferenceEquals(_lastOwner, this) && ReferenceEquals(_lastSource, source) && ReferenceEquals(_lastDestination, destination))
            return new StatsHandle(_lastStats!);

        PairStats stats;
        if (_pairs.TryGetValue((source, destination), out var existing)) stats = existing;
        else if (_pairs.Count >= _options.MaxTrackedPairs) stats = _overflow;
        else stats = _pairs.GetOrAdd((source, destination), static _ => new PairStats());

        _lastOwner = this;
        _lastSource = source;
        _lastDestination = destination;
        _lastStats = stats;
        return new StatsHandle(stats);
    }

    private readonly struct StatsHandle
    {
        private readonly PairStats _stats;
        public StatsHandle(PairStats stats) => _stats = stats;

        public void Record(long timestampTicks, bool failed)
        {
            Interlocked.Increment(ref _stats.Calls);
            if (failed) Interlocked.Increment(ref _stats.Errors);
            var ticks = (long)(timestampTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
            Interlocked.Add(ref _stats.TotalTicks, ticks);
            long seen;
            while (ticks > (seen = Volatile.Read(ref _stats.MaxTicks)) && Interlocked.CompareExchange(ref _stats.MaxTicks, ticks, seen) != seen) { }
            _stats.Histogram.Record(ticks / (double)TimeSpan.TicksPerMillisecond * 1000.0);
        }
    }

    private void RecordException(Type source, Type destination, Exception exception)
    {
        try
        {
            var root = exception is AggregateException or System.Reflection.TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception;
            var frames = TopFrames(root, _options.StackFramesInGroupKey);
            var key = $"{root.GetType().FullName}|{source.FullName}>{destination.FullName}|{string.Join(">", frames)}";
            var now = _time.GetUtcNow().UtcTicks;

            if (!_exceptions.TryGetValue(key, out var group))
            {
                if (_exceptions.Count >= _options.MaxExceptionGroups) { Interlocked.Increment(ref _droppedExceptionGroups); return; }
                group = _exceptions.GetOrAdd(key, _ => new ExceptionGroup
                {
                    Key = key,
                    Type = root.GetType().FullName ?? root.GetType().Name,
                    Source = Pretty(source),
                    Destination = Pretty(destination),
                    FirstTicks = now,
                    Frames = frames,
                    Message = SafeMessage(root)
                });
            }
            Interlocked.Increment(ref group.Count);
            Interlocked.Exchange(ref group.LastTicks, now);
        }
        catch
        {
            // never let diagnostics throw into a mapping call
        }
    }

    private string? SafeMessage(Exception ex)
    {
        if (!_options.CaptureExceptionMessages && ex is not AutoMappingException) return null;
        var message = ex.Message;
        if (ex is not AutoMappingException && _options.MessageRedactor is { } redact)
        {
            try { message = redact(message); }
            catch { return null; }   // a failing redactor must never leak the raw message
        }
        return message.Length <= _options.MaxMessageLength ? message : message[.._options.MaxMessageLength] + "…";
    }

    // method names only: no file paths, no arguments, no values
    private static string[] TopFrames(Exception ex, int count)
    {
        var frames = new StackTrace(ex, fNeedFileInfo: false).GetFrames();
        var names = new List<string>(count);
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method == null) continue;
            names.Add($"{method.DeclaringType?.Name}.{method.Name}");
            if (names.Count == count) break;
        }
        return names.ToArray();
    }

    internal static string Pretty(Type t) =>
        t.IsGenericType ? $"{t.Name[..t.Name.IndexOf('`')]}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>" : t.Name;

    public TelemetrySnapshot Snapshot()
    {
        double ToMs(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

        var pairs = _pairs.Select(kv => new PairSnapshot(Pretty(kv.Key.Item1), Pretty(kv.Key.Item2),
            Volatile.Read(ref kv.Value.Calls), Volatile.Read(ref kv.Value.Errors), ToMs(Volatile.Read(ref kv.Value.TotalTicks)),
            ToMs(Volatile.Read(ref kv.Value.MaxTicks)), kv.Value.Histogram.Snapshot())).ToList();

        var overflowCalls = Volatile.Read(ref _overflow.Calls);
        if (overflowCalls > 0)
            pairs.Add(new PairSnapshot("(other)", "(other)", overflowCalls, Volatile.Read(ref _overflow.Errors),
                ToMs(Volatile.Read(ref _overflow.TotalTicks)), ToMs(Volatile.Read(ref _overflow.MaxTicks)), _overflow.Histogram.Snapshot()));

        var exceptions = _exceptions.Values.Select(g => new ExceptionSnapshot(g.Key, g.Type, g.Source, g.Destination, Volatile.Read(ref g.Count),
            new DateTimeOffset(g.FirstTicks, TimeSpan.Zero), new DateTimeOffset(Volatile.Read(ref g.LastTicks), TimeSpan.Zero), g.Message, g.Frames)).ToList();

        var compiles = _compiles.Select(kv =>
        {
            lock (kv.Value) return new CompileSnapshot(Pretty(kv.Key.Item1), Pretty(kv.Key.Item2), kv.Value.Compiles, ToMs(kv.Value.TotalTicks), ToMs(kv.Value.MaxTicks));
        }).ToList();

        return new TelemetrySnapshot(_time.GetUtcNow(), pairs, exceptions, compiles, Volatile.Read(ref _droppedExceptionGroups), overflowCalls);
    }
}
