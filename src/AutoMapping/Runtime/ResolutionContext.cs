using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AutoMapping;

/// <summary>
/// State for one top-level <c>Map</c> call. Passed to resolvers, converters, hooks and conditions.
/// <see cref="Items"/> carries caller supplied values; <see cref="Mapper"/> maps nested objects inside the same operation.
/// </summary>
public sealed class ResolutionContext
{
    private readonly AutoMapping.Mapper _root;
    private readonly int _maxDepth;
    private int _depth;
    private int _hookDepth = 1;
    private Dictionary<(object Source, Type Destination), object>? _cache;
    private Dictionary<int, int>? _typeMapDepth;
    private Dictionary<string, object?>? _items;
    private IMapper? _scoped;

    internal ResolutionContext(AutoMapping.Mapper root, int maxDepth, Func<Type, object> serviceCtor)
    {
        _root = root;
        _maxDepth = maxDepth;
        ServiceCtor = serviceCtor;
    }

    internal Func<Type, object> ServiceCtor { get; set; }
    // fields (not properties) so compiled mappings can test them inline
    internal List<Action<object?, object?>>? OperationBefore;
    internal List<Action<object?, object?>>? OperationAfter;

    /// <summary>Caller supplied values for this operation (<c>mapper.Map&lt;T&gt;(src, o =&gt; o.Items["k"] = v)</c>).</summary>
    public IDictionary<string, object?> Items => _items ??= new Dictionary<string, object?>();

    /// <summary>A mapper bound to this operation: nested maps share <see cref="Items"/> and cycle tracking.</summary>
    public IMapper Mapper => _scoped ??= new ScopedMapper(_root, this);

    /// <summary>Creates a resolver/converter instance through the configured service constructor (DI aware).</summary>
    public object CreateInstance(Type type) => ServiceCtor(type);

    /// <summary>Parameterless construction used by dictionary-to-object mapping (value types and classes with a default constructor).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public object CreateInstanceOrDefault(Type type)
    {
        try { return Activator.CreateInstance(type, nonPublic: true)!; }
        catch (MissingMethodException)
        {
            throw new MappingException($"{type.Name} needs a parameterless constructor to be mapped from a dictionary.", typeof(object), type);
        }
    }

    // ---- used by compiled mapping expressions -------------------------------------------------------------

    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enter(int typeMapId)
    {
        if (++_depth > _maxDepth) ThrowDepthExceeded(typeMapId);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowDepthExceeded(int typeMapId)
    {
        var map = _root.Configuration.FindTypeMapById(typeMapId);
        var source = map?.SourceType ?? typeof(object);
        var destination = map?.DestinationType ?? typeof(object);
        throw new MappingException(
            $"Maximum mapping depth ({_maxDepth}) exceeded while mapping {source.Name} to {destination.Name}. " +
            "The object graph is probably cyclic; call PreserveReferences() on the map.",
            source, destination);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit() => _depth--;

    /// <summary>Current nesting depth; restored by the context-bound mapper when a nested call throws.</summary>
    internal int Depth
    {
        get => _depth;
        set => _depth = value;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool TryGetCached(object source, Type destination, out object? value)
    {
        if (_cache != null && _cache.TryGetValue((source, destination), out var v))
        {
            value = v;
            return true;
        }
        value = null;
        return false;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void Remember(object source, Type destination, object result)
    {
        _cache ??= new Dictionary<(object, Type), object>(new CacheComparer());
        _cache[(source, destination)] = result;
    }

    /// <summary>Per-map MaxDepth: returns false (and does not enter) when the map is already <paramref name="max"/> levels deep.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool EnterTypeMap(int id, int max)
    {
        _typeMapDepth ??= new Dictionary<int, int>();
        _typeMapDepth.TryGetValue(id, out var current);
        if (current >= max) return false;
        _typeMapDepth[id] = current + 1;
        return true;
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void ExitTypeMap(int id) => _typeMapDepth![id]--;

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void OnBeforeMap(object? source, object? destination)
    {
        if (OperationBefore == null || _depth != _hookDepth) return;
        foreach (var action in OperationBefore) action(source, destination);
    }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public void OnAfterMap(object? source, object? destination)
    {
        if (OperationAfter == null || _depth != _hookDepth) return;
        foreach (var action in OperationAfter) action(source, destination);
    }

    /// <summary>
    /// Starts a nested operation with its own per-call options (hooks, service constructor, items). Items are merged into the
    /// shared bag; the previous options are restored when the returned scope is disposed.
    /// </summary>
    internal IDisposable BeginNested(IDictionary<string, object?> items, List<Action<object?, object?>>? before,
        List<Action<object?, object?>>? after, Func<Type, object>? serviceCtor)
    {
        var scope = new NestedScope(this, OperationBefore, OperationAfter, ServiceCtor, _hookDepth);
        foreach (var item in items) Items[item.Key] = item.Value;
        OperationBefore = before;
        OperationAfter = after;
        if (serviceCtor != null) ServiceCtor = serviceCtor;
        _hookDepth = _depth + 1;
        return scope;
    }

    private sealed class NestedScope : IDisposable
    {
        private readonly ResolutionContext _c;
        private readonly List<Action<object?, object?>>? _before, _after;
        private readonly Func<Type, object> _ctor;
        private readonly int _hookDepth;

        public NestedScope(ResolutionContext c, List<Action<object?, object?>>? before, List<Action<object?, object?>>? after,
            Func<Type, object> ctor, int hookDepth)
        {
            _c = c; _before = before; _after = after; _ctor = ctor; _hookDepth = hookDepth;
        }

        public void Dispose()
        {
            _c.OperationBefore = _before;
            _c.OperationAfter = _after;
            _c.ServiceCtor = _ctor;
            _c._hookDepth = _hookDepth;
        }
    }

    /// <summary>Maps using the runtime type of <paramref name="source"/> (MapAtRuntime / polymorphic members).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public object? MapRuntime(object? source, Type destinationType)
    {
        if (source is null) return destinationType.IsValueType ? Activator.CreateInstance(destinationType) : null;
        return _root.Configuration.GetMapping(source.GetType(), destinationType).MapObject(source, null, this);
    }

    private sealed class CacheComparer : IEqualityComparer<(object Source, Type Destination)>
    {
        public bool Equals((object Source, Type Destination) x, (object Source, Type Destination) y) =>
            ReferenceEquals(x.Source, y.Source) && x.Destination == y.Destination;

        public int GetHashCode((object Source, Type Destination) obj) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Source), obj.Destination);
    }
}
