using System.ComponentModel;

namespace AutoMapping.Internal;

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class MappingBase
{
    public abstract object? MapObject(object? source, object? destination, ResolutionContext context);
    public abstract object? MapObjectLean(object? source, object? destination);
    /// <summary>True when the map needs no <see cref="ResolutionContext"/> (no delegates, hooks, cycles...), so it can run without allocating one.</summary>
    internal abstract bool IsLean { get; }
    internal abstract void EnsureCompiled();
}

/// <summary>
/// Lazily compiled mapping delegate for one (TSource, TDestination) pair. Compiled expression trees call
/// each other through these holders, which is what makes recursive and cyclic type graphs possible.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class Mapping<TSource, TDestination> : MappingBase
{
    private readonly MapperConfiguration _config;
    private readonly TypeMap? _typeMap;
    private readonly object _lock = new();
    private volatile Func<TSource, TDestination, ResolutionContext, TDestination>? _compiled;
    private volatile Func<TSource, TDestination, TDestination>? _leanCompiled;
    private int _leanState;   // 0 unknown, 1 lean, 2 needs a context

    internal Mapping(MapperConfiguration config, TypeMap? typeMap)
    {
        _config = config;
        _typeMap = typeMap;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public TDestination Map(TSource source, TDestination destination, ResolutionContext context) =>
        (_compiled ?? Compile())(source, destination, context);

    public override object? MapObject(object? source, object? destination, ResolutionContext context) =>
        Map((TSource)source!, destination is null ? default! : (TDestination)destination, context);

    internal override bool IsLean
    {
        get
        {
            var state = _leanState;
            if (state == 0)
            {
                state = _config.IsLeanMapping(_typeMap, typeof(TSource), typeof(TDestination)) ? 1 : 2;
                _leanState = state;
            }
            return state == 1;
        }
    }

    /// <summary>Context-free entry; only valid when <see cref="IsLean"/> is true.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public TDestination MapLean(TSource source, TDestination destination) =>
        (_leanCompiled ?? CompileLean())(source, destination);

    internal Func<TSource, TDestination, TDestination> LeanDelegate => _leanCompiled ?? CompileLean();

    public override object? MapObjectLean(object? source, object? destination) =>
        MapLean((TSource)source!, destination is null ? default! : (TDestination)destination);

    private Func<TSource, TDestination, TDestination> CompileLean()
    {
        lock (_lock)
        {
            if (_leanCompiled != null) return _leanCompiled;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var builder = _config.CreateBuilder(_typeMap, lean: true);
            var inner = _typeMap != null
                ? builder.BuildTypeMapLambda(_typeMap)
                : builder.BuildConversionLambda(typeof(TSource), typeof(TDestination));

            // the context parameter is always null here; the tree is built so that nothing dereferences it
            var source = System.Linq.Expressions.Expression.Parameter(typeof(TSource), "src");
            var destination = System.Linq.Expressions.Expression.Parameter(typeof(TDestination), "dest");
            var outer = System.Linq.Expressions.Expression.Lambda<Func<TSource, TDestination, TDestination>>(
                System.Linq.Expressions.Expression.Invoke(inner, source, destination,
                    System.Linq.Expressions.Expression.Constant(null, typeof(ResolutionContext))),
                source, destination);
            _leanCompiled = outer.Compile();
            _config.NotifyCompiled(typeof(TSource), typeof(TDestination), System.Diagnostics.Stopwatch.GetElapsedTime(started), lean: true);
            return _leanCompiled;
        }
    }

    internal override void EnsureCompiled()
    {
        Compile();
        if (IsLean) CompileLean();
    }

    private Func<TSource, TDestination, ResolutionContext, TDestination> Compile()
    {
        var compiled = _compiled;
        if (compiled != null) return compiled;

        lock (_lock)
        {
            if (_compiled != null) return _compiled;
            if (_typeMap is { ProjectionOnly: true })
                throw new MappingConfigurationException(
                    $"{_typeMap.Pair} was created with CreateProjection() and can only be used with ProjectTo. Use CreateMap() for runtime mapping.");
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var builder = _config.CreateBuilder(_typeMap);
            var lambda = _typeMap != null
                ? builder.BuildTypeMapLambda(_typeMap)
                : builder.BuildConversionLambda(typeof(TSource), typeof(TDestination));
            _compiled = (Func<TSource, TDestination, ResolutionContext, TDestination>)lambda.Compile();
            _config.NotifyCompiled(typeof(TSource), typeof(TDestination), System.Diagnostics.Stopwatch.GetElapsedTime(started), lean: false);
            return _compiled;
        }
    }
}
