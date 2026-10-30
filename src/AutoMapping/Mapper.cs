using System.Linq.Expressions;
using AutoMapping.Internal;

namespace AutoMapping;

public interface IMapper
{
    IConfigurationProvider ConfigurationProvider { get; }

    /// <summary>Creates resolvers, converters and actions (DI aware when registered through <c>AddAutoMapping</c>).</summary>
    Func<Type, object> ServiceCtor { get; }

    /// <summary>Maps using the runtime type of <paramref name="source"/>.</summary>
    TDestination Map<TDestination>(object? source);
    TDestination Map<TDestination>(object? source, Action<IMappingOperationOptions<object, TDestination>> opts);

    TDestination Map<TSource, TDestination>(TSource source);
    TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions<TSource, TDestination>> opts);

    /// <summary>Maps onto an existing destination instance.</summary>
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination);
    TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions<TSource, TDestination>> opts);

    object? Map(object? source, Type sourceType, Type destinationType);
    object? Map(object? source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts);
    object? Map(object? source, object? destination, Type sourceType, Type destinationType);
    object? Map(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts);

    /// <summary>Builds a <c>Select</c> projection the query provider (EF Core, ...) can translate to SQL.</summary>
    IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source, object? parameters = null,
        params Expression<Func<TDestination, object>>[] membersToExpand);
}

/// <summary>Thread-safe and stateless; register as a singleton, or per scope when resolvers need scoped services.</summary>
public sealed class Mapper : IMapper
{
    private readonly int _maxDepth;

    public Mapper(IConfigurationProvider configuration) : this(configuration, null)
    {
    }

    public Mapper(IConfigurationProvider configuration, Func<Type, object>? serviceCtor)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Configuration = configuration as MapperConfiguration
                        ?? throw new ArgumentException("Only a AutoMapping MapperConfiguration is supported.", nameof(configuration));
        ServiceCtor = serviceCtor ?? Configuration.Options.ServiceCtor;
        _maxDepth = Configuration.Options.MaxDepth;
    }

    internal MapperConfiguration Configuration { get; }

    public IConfigurationProvider ConfigurationProvider => Configuration;

    public Func<Type, object> ServiceCtor { get; }

    // ------------------------------------------------------------------ context creation

    internal ResolutionContext NewContext(Func<Type, object>? serviceCtor = null) =>
        new(this, _maxDepth, serviceCtor ?? ServiceCtor);

    private ResolutionContext Prepare<TSource, TDestination>(Action<IMappingOperationOptions<TSource, TDestination>> opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var options = new MappingOperationOptions<TSource, TDestination>();
        opts(options);
        return options.Apply(NewContext(options.ServiceCtor));
    }

    private ResolutionContext Prepare(Action<IMappingOperationOptions> opts)
    {
        ArgumentNullException.ThrowIfNull(opts);
        var options = new MappingOperationOptions();
        opts(options);
        return options.Apply(NewContext(options.ServiceCtor));
    }

    // ------------------------------------------------------------------ core (shared with the context-bound mapper)

    // ---- context-free fast path: maps proven not to need a ResolutionContext skip allocating one -----------------------

    private TDestination MapCore<TDestination>(object? source)
    {
        if (source is null) return default!;
        var mapping = Configuration.GetMapping(source.GetType(), typeof(TDestination));
        return (TDestination)(mapping.IsLean ? mapping.MapObjectLean(source, null) : mapping.MapObject(source, null, NewContext()))!;
    }

    private TDestination MapCore<TSource, TDestination>(TSource source, TDestination destination)
    {
        var lean = Configuration.TryGetLean<TSource, TDestination>();
        if (lean != null) return lean(source, destination);
        return MapCoreSlow(source, destination);
    }

    private TDestination MapCoreSlow<TSource, TDestination>(TSource source, TDestination destination)
    {
        var lean = Configuration.ResolveLean<TSource, TDestination>();
        if (lean != null) return lean(source, destination);
        return Configuration.GetMapping<TSource, TDestination>().Map(source, destination, NewContext());
    }

    private object? MapCore(object? source, object? destination, Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);
        if (source is null) return destination ?? (destinationType.IsValueType ? Activator.CreateInstance(destinationType) : null);
        var mapping = Configuration.GetMapping(sourceType, destinationType);
        return mapping.IsLean ? mapping.MapObjectLean(source, destination) : mapping.MapObject(source, destination, NewContext());
    }

    internal TDestination MapCore<TDestination>(object? source, ResolutionContext ctx)
    {
        if (source is null) return default!;
        return (TDestination)Configuration.GetMapping(source.GetType(), typeof(TDestination)).MapObject(source, null, ctx)!;
    }

    internal TDestination MapCore<TSource, TDestination>(TSource source, TDestination destination, ResolutionContext ctx) =>
        Configuration.GetMapping<TSource, TDestination>().Map(source, destination, ctx);

    internal object? MapCore(object? source, object? destination, Type sourceType, Type destinationType, ResolutionContext ctx)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);
        if (source is null) return destination ?? (destinationType.IsValueType ? Activator.CreateInstance(destinationType) : null);
        return Configuration.GetMapping(sourceType, destinationType).MapObject(source, destination, ctx);
    }

    // ------------------------------------------------------------------ public API

    // With an IMapperObserver attached every top-level call is timed and failures are reported; without one: one null check.
    // The observed variants avoid lambdas so that turning reporting on does not add closure allocations to every call.
    private void NotifyMapped(Type source, Type destination, long started)
    {
        try { Configuration.Observer?.OnMapped(source, destination, System.Diagnostics.Stopwatch.GetTimestamp() - started); }
        catch { /* diagnostics must never break mapping */ }
    }

    private void NotifyFailed(Type source, Type destination, long started, Exception exception)
    {
        try { Configuration.Observer?.OnFailed(source, destination, System.Diagnostics.Stopwatch.GetTimestamp() - started, exception); }
        catch { /* diagnostics must never break mapping */ }
    }

    // The observed variants live in their own methods: a lambda in the public method would allocate its closure on every call.
    private const System.Runtime.CompilerServices.MethodImplOptions Slow = System.Runtime.CompilerServices.MethodImplOptions.NoInlining;

    public TDestination Map<TDestination>(object? source)
    {
        if (Configuration.Observer is null) return MapCore<TDestination>(source);
        return MapObserved<TDestination>(source);
    }
    [System.Runtime.CompilerServices.MethodImpl(Slow)]
    private TDestination MapObserved<TDestination>(object? source)
    {
        var sourceType = source?.GetType() ?? typeof(object);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { var result = MapCore<TDestination>(source); NotifyMapped(sourceType, typeof(TDestination), started); return result; }
        catch (Exception ex) { NotifyFailed(sourceType, typeof(TDestination), started, ex); throw; }
    }

    public TDestination Map<TDestination>(object? source, Action<IMappingOperationOptions<object, TDestination>> opts)
    {
        if (Configuration.Observer is null) return MapCore<TDestination>(source, Prepare(opts));
        return MapObserved(source, opts);
    }
    [System.Runtime.CompilerServices.MethodImpl(Slow)]
    private TDestination MapObserved<TDestination>(object? source, Action<IMappingOperationOptions<object, TDestination>> opts)
    {
        var sourceType = source?.GetType() ?? typeof(object);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { var result = MapCore<TDestination>(source, Prepare(opts)); NotifyMapped(sourceType, typeof(TDestination), started); return result; }
        catch (Exception ex) { NotifyFailed(sourceType, typeof(TDestination), started, ex); throw; }
    }

    public TDestination Map<TSource, TDestination>(TSource source)
    {
        if (Configuration.Observer is null) return MapCore<TSource, TDestination>(source, default!);
        return MapObserved<TSource, TDestination>(source, default!);
    }

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination)
    {
        if (Configuration.Observer is null) return MapCore<TSource, TDestination>(source, destination);
        return MapObserved(source, destination);
    }
    [System.Runtime.CompilerServices.MethodImpl(Slow)]
    private TDestination MapObserved<TSource, TDestination>(TSource source, TDestination destination)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { var result = MapCore<TSource, TDestination>(source, destination); NotifyMapped(typeof(TSource), typeof(TDestination), started); return result; }
        catch (Exception ex) { NotifyFailed(typeof(TSource), typeof(TDestination), started, ex); throw; }
    }

    public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions<TSource, TDestination>> opts)
    {
        if (Configuration.Observer is null) return MapCore(source, default(TDestination)!, Prepare(opts));
        return MapObserved(source, default!, opts);
    }

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions<TSource, TDestination>> opts)
    {
        if (Configuration.Observer is null) return MapCore(source, destination, Prepare(opts));
        return MapObserved(source, destination, opts);
    }
    [System.Runtime.CompilerServices.MethodImpl(Slow)]
    private TDestination MapObserved<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions<TSource, TDestination>> opts)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { var result = MapCore(source, destination, Prepare(opts)); NotifyMapped(typeof(TSource), typeof(TDestination), started); return result; }
        catch (Exception ex) { NotifyFailed(typeof(TSource), typeof(TDestination), started, ex); throw; }
    }

    public object? Map(object? source, Type sourceType, Type destinationType) => Map(source, null, sourceType, destinationType);

    public object? Map(object? source, object? destination, Type sourceType, Type destinationType)
    {
        if (Configuration.Observer is null) return MapCore(source, destination, sourceType, destinationType);
        return MapObserved(source, destination, sourceType, destinationType);
    }
    [System.Runtime.CompilerServices.MethodImpl(Slow)]
    private object? MapObserved(object? source, object? destination, Type sourceType, Type destinationType)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { var result = MapCore(source, destination, sourceType, destinationType); NotifyMapped(sourceType, destinationType, started); return result; }
        catch (Exception ex) { NotifyFailed(sourceType, destinationType, started, ex); throw; }
    }

    public object? Map(object? source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) =>
        Map(source, null, sourceType, destinationType, opts);

    public object? Map(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts)
    {
        if (Configuration.Observer is null) return MapCore(source, destination, sourceType, destinationType, Prepare(opts));
        return MapObserved(source, destination, sourceType, destinationType, opts);
    }
    [System.Runtime.CompilerServices.MethodImpl(Slow)]
    private object? MapObserved(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { var result = MapCore(source, destination, sourceType, destinationType, Prepare(opts)); NotifyMapped(sourceType, destinationType, started); return result; }
        catch (Exception ex) { NotifyFailed(sourceType, destinationType, started, ex); throw; }
    }

    public IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source, object? parameters = null,
        params Expression<Func<TDestination, object>>[] membersToExpand) =>
        source.ProjectTo(Configuration, parameters, membersToExpand);
}

/// <summary>Handed to resolvers through <see cref="ResolutionContext.Mapper"/>: nested maps share the operation's Items and cycle tracking.</summary>
internal sealed class ScopedMapper : IMapper
{
    private readonly Mapper _root;
    private readonly ResolutionContext _context;

    public ScopedMapper(Mapper root, ResolutionContext context)
    {
        _root = root;
        _context = context;
    }

    // A resolver may catch an exception thrown by a nested map; the aborted levels never ran Exit, so the depth is put back.
    private T Guard<T>(Func<T> run)
    {
        var depth = _context.Depth;
        try { return run(); }
        catch { _context.Depth = depth; throw; }
    }

    public IConfigurationProvider ConfigurationProvider => _root.ConfigurationProvider;
    public Func<Type, object> ServiceCtor => _context.ServiceCtor;

    // Per-call options on a nested map apply to that nested operation only (Items are shared, hooks/service ctor are restored afterwards).
    private T Nested<TSource, TDestination, T>(Action<IMappingOperationOptions<TSource, TDestination>> opts, Func<T> run)
    {
        var options = new MappingOperationOptions<TSource, TDestination>();
        opts(options);
        using (_context.BeginNested(options.Items, options.Before, options.After, options.ServiceCtor)) return Guard(run);
    }

    private T Nested<T>(Action<IMappingOperationOptions> opts, Func<T> run)
    {
        var options = new MappingOperationOptions();
        opts(options);
        using (_context.BeginNested(options.Items, options.Before, options.After, options.ServiceCtor)) return Guard(run);
    }

    public TDestination Map<TDestination>(object? source) => Guard(() => _root.MapCore<TDestination>(source, _context));

    public TDestination Map<TDestination>(object? source, Action<IMappingOperationOptions<object, TDestination>> opts) =>
        Nested(opts, () => _root.MapCore<TDestination>(source, _context));

    public TDestination Map<TSource, TDestination>(TSource source) => Guard(() => _root.MapCore(source, default(TDestination)!, _context));

    public TDestination Map<TSource, TDestination>(TSource source, Action<IMappingOperationOptions<TSource, TDestination>> opts) =>
        Nested(opts, () => _root.MapCore(source, default(TDestination)!, _context));

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination) => Guard(() => _root.MapCore(source, destination, _context));

    public TDestination Map<TSource, TDestination>(TSource source, TDestination destination, Action<IMappingOperationOptions<TSource, TDestination>> opts) =>
        Nested(opts, () => _root.MapCore(source, destination, _context));

    public object? Map(object? source, Type sourceType, Type destinationType) =>
        _root.MapCore(source, null, sourceType, destinationType, _context);

    public object? Map(object? source, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) =>
        Nested(opts, () => _root.MapCore(source, null, sourceType, destinationType, _context));

    public object? Map(object? source, object? destination, Type sourceType, Type destinationType) =>
        _root.MapCore(source, destination, sourceType, destinationType, _context);

    public object? Map(object? source, object? destination, Type sourceType, Type destinationType, Action<IMappingOperationOptions> opts) =>
        Nested(opts, () => _root.MapCore(source, destination, sourceType, destinationType, _context));

    public IQueryable<TDestination> ProjectTo<TDestination>(IQueryable source, object? parameters = null,
        params Expression<Func<TDestination, object>>[] membersToExpand) =>
        _root.ProjectTo(source, parameters, membersToExpand);
}

internal class MappingOperationOptions<TSource, TDestination> : IMappingOperationOptions<TSource, TDestination>
{
    private List<Action<object?, object?>>? _before;
    private List<Action<object?, object?>>? _after;

    public List<Action<object?, object?>>? Before => _before;
    public List<Action<object?, object?>>? After => _after;

    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>();
    public Func<Type, object>? ServiceCtor { get; private set; }

    public void BeforeMap(Action<TSource, TDestination> beforeFunction)
    {
        ArgumentNullException.ThrowIfNull(beforeFunction);
        (_before ??= new()).Add((s, d) => beforeFunction((TSource)s!, (TDestination)d!));
    }

    public void AfterMap(Action<TSource, TDestination> afterFunction)
    {
        ArgumentNullException.ThrowIfNull(afterFunction);
        (_after ??= new()).Add((s, d) => afterFunction((TSource)s!, (TDestination)d!));
    }

    public void ConstructServicesUsing(Func<Type, object> constructor) =>
        ServiceCtor = constructor ?? throw new ArgumentNullException(nameof(constructor));

    public ResolutionContext Apply(ResolutionContext context)
    {
        foreach (var item in Items) context.Items[item.Key] = item.Value;
        context.OperationBefore = _before;
        context.OperationAfter = _after;
        return context;
    }
}

internal sealed class MappingOperationOptions : MappingOperationOptions<object, object>, IMappingOperationOptions
{
}

public static class QueryableExtensions
{
    /// <summary>
    /// AutoMapper-style projection: <c>dbContext.Users.ProjectTo&lt;UserDto&gt;(config)</c>. Anonymous-object <paramref name="parameters"/>
    /// replace captured variables of the same name inside <c>MapFrom</c> expressions; <paramref name="membersToExpand"/> opts
    /// in to members marked <c>ExplicitExpansion()</c>.
    /// </summary>
    public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IConfigurationProvider configuration,
        object? parameters = null, params Expression<Func<TDestination, object>>[] membersToExpand)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        var config = configuration as MapperConfiguration
                     ?? throw new ArgumentException("Only a AutoMapping MapperConfiguration is supported.", nameof(configuration));

        ISet<string>? expand = null;
        if (membersToExpand is { Length: > 0 })
            expand = new HashSet<string>(membersToExpand.Select(ProjectionBuilder.PathOf), StringComparer.Ordinal);

        var lambda = config.GetProjection(source.ElementType, typeof(TDestination), expand);
        if (parameters != null) lambda = ProjectionBuilder.Parameterize(lambda, parameters);

        var select = Expression.Call(
            typeof(Queryable), nameof(Queryable.Select),
            new[] { source.ElementType, typeof(TDestination) },
            source.Expression, Expression.Quote(lambda));
        return source.Provider.CreateQuery<TDestination>(select);
    }

    public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IMapper mapper,
        object? parameters = null, params Expression<Func<TDestination, object>>[] membersToExpand) =>
        source.ProjectTo(ArgumentNullGuard(mapper).ConfigurationProvider, parameters, membersToExpand);

    private static IMapper ArgumentNullGuard(IMapper mapper) => mapper ?? throw new ArgumentNullException(nameof(mapper));
}
