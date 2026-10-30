using System.Diagnostics;

namespace AutoMapping;

/// <summary>
/// Receives one callback per top-level <c>IMapper.Map</c> call and per compiled map, for metrics and reports.
/// Attach with <see cref="MapperConfiguration.Observer"/>. With no observer the mapping path pays a single null check.
/// Implementations must be thread-safe and fast; an exception thrown from a callback is swallowed (diagnostics never break mapping).
/// </summary>
public interface IMapperObserver
{
    /// <param name="source">The source type.</param>
    /// <param name="destination">The destination type.</param>
    /// <param name="elapsedTimestampTicks">Elapsed <see cref="Stopwatch"/> ticks (use <see cref="Stopwatch.Frequency"/> to convert).</param>
    void OnMapped(Type source, Type destination, long elapsedTimestampTicks);

    void OnFailed(Type source, Type destination, long elapsedTimestampTicks, Exception exception);

    void OnCompiled(Type source, Type destination, TimeSpan duration, bool contextFree);
}

/// <summary>Sizes of the internal caches; growth over time without new maps being configured points at runtime-type churn.</summary>
public readonly record struct MapperCacheStatistics(int TypeMaps, int Mappings, int Projections, int ContextFreeAnalyses, int FastLookupSlots);
