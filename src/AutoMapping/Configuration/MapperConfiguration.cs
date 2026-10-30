using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using AutoMapping.Internal;

namespace AutoMapping;

/// <summary>
/// Immutable, thread-safe holder of all maps. Create once (at startup), share everywhere.
/// Maps are resolved eagerly (so configuration errors surface immediately) and compiled lazily on first use;
/// call <see cref="CompileMappings"/> to pay the compile cost at startup instead.
/// </summary>
public sealed class MapperConfiguration : IConfigurationProvider
{
    private readonly ConcurrentDictionary<TypePair, TypeMap> _typeMaps = new();
    private readonly ConcurrentDictionary<TypePair, MappingBase> _mappings = new();
    private readonly ConcurrentDictionary<TypePair, LambdaExpression> _projections = new();
    private readonly Dictionary<Profile, ProfileSettings> _settings = new();
    private readonly List<OpenGenericMap> _openMaps = new();
    private readonly Lazy<MlMemberMatcher> _ml = new(() => new MlMemberMatcher(), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly object _createLock = new();
    private readonly ProfileSettings _rootSettings;

    internal MapperConfigurationExpression Options { get; }

    public MapperConfiguration(Action<IMapperConfigurationExpression> configure)
        : this(Configure(configure))
    {
    }

    /// <summary>AutoMapper-compatible overload. The logger factory is accepted for source compatibility and not used.</summary>
    public MapperConfiguration(Action<IMapperConfigurationExpression> configure, Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory)
        : this(configure)
    {
    }

    public MapperConfiguration(MapperConfigurationExpression configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Options = configuration;

        var profiles = new List<Profile> { configuration };
        profiles.AddRange(configuration.Profiles);

        foreach (var profile in profiles)
        {
            var settings = new ProfileSettings(profile, configuration);
            _settings[profile] = settings;
            _openMaps.AddRange(profile.OpenGenericMaps);

            foreach (var typeMap in profile.TypeMaps)
            {
                typeMap.Settings = settings;
                _typeMaps[typeMap.Pair] = typeMap;   // duplicate CreateMap for a pair: the last one wins (same as AutoMapper)
            }
        }
        _rootSettings = _settings[configuration];

        // ForAllMaps: root actions reach every profile's maps, a profile's own actions only its maps
        foreach (var profile in profiles)
        {
            var actions = profile == configuration ? configuration.AllMapsActions : configuration.AllMapsActions.Concat(profile.AllMapsActions).ToList();
            if (actions.Count == 0) continue;
            foreach (var typeMap in profile.TypeMaps.ToList())
            {
                var info = new TypeMapInfo(typeMap);
                var expression = new NonGenericMappingExpression(typeMap, null, profile);
                foreach (var action in actions) action(info, expression);
            }
        }

        LinkInheritance();
        foreach (var typeMap in _typeMaps.Values.ToList()) Finish(typeMap);
    }

    private static MapperConfigurationExpression Configure(Action<IMapperConfigurationExpression> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var expression = new MapperConfigurationExpression();
        configure(expression);
        return expression;
    }

    public IMapper CreateMapper() => new Mapper(this, Options.ServiceCtor);

    public IMapper CreateMapper(Func<Type, object> serviceCtor) => new Mapper(this, serviceCtor);

    internal ExpressionBuilder CreateBuilder(TypeMap? typeMap, bool lean = false) => new(this, typeMap?.Settings ?? _rootSettings, lean);

    // ------------------------------------------------------------------ context-free ("lean") analysis

    private readonly ConcurrentDictionary<int, bool> _leanMaps = new();

    /// <summary>Test/diagnostic switch: disables the context-free fast path so both paths can be compared.</summary>
    internal bool DisableLeanPath { get; set; }

    internal bool IsLeanMapping(TypeMap? typeMap, Type source, Type destination)
    {
        if (DisableLeanPath) return false;
        return typeMap != null
            ? IsLeanMap(typeMap, new HashSet<int>())
            : !_rootSettings.CreateMissingTypeMaps && IsLeanConversion(source, destination, new HashSet<int>());
    }

    /// <summary>
    /// A map is lean when running it needs nothing from the <see cref="ResolutionContext"/>: no user delegates (they receive the
    /// context), no hooks, no reference tracking or depth limits, and no way to reach itself again (cycles need depth tracking).
    /// Everything it maps to must be lean too. The analysis is conservative: anything unknown makes the map non-lean.
    /// </summary>
    private bool IsLeanMap(TypeMap tm, HashSet<int> path)
    {
        if (_leanMaps.TryGetValue(tm.Id, out var known)) return known;
        if (!path.Add(tm.Id)) return false;   // reached itself: recursive graph, depth tracking needed

        var lean = ComputeLeanMap(tm, path);
        path.Remove(tm.Id);
        _leanMaps[tm.Id] = lean;
        return lean;
    }

    private bool ComputeLeanMap(TypeMap tm, HashSet<int> path)
    {
        if (tm.ProjectionOnly || tm.Settings.CreateMissingTypeMaps) return false;
        if (tm.ConverterType != null || tm.AsType != null) return false;
        if (tm.CustomConverter != null && tm.ConvertExpression == null) return false;   // delegates receive the context
        if (tm.ConvertExpression != null) return true;
        if (tm.ConstructUsing != null || tm.ConstructUsingServiceLocator) return false;
        if (tm.BeforeMaps.Count > 0 || tm.AfterMaps.Count > 0) return false;
        if (tm.PreserveReferences || tm.MaxDepth > 0) return false;
        if (tm.DerivedTypeMaps.Count > 0) return false;   // polymorphic dispatch

        foreach (var pm in tm.PropertyMaps.Concat(tm.CtorParams))
        {
            if (pm.Ignored || !pm.IsResolved || pm.DestinationMember == null && !tm.CtorParams.Contains(pm)) continue;
            if (pm.Resolver != null || pm.Condition != null || pm.MapAtRuntime) return false;
            if (pm.PreCondition != null && pm.WhenExpression == null) return false;

            Type? sourceType;
            if (pm.CustomSource != null) sourceType = pm.CustomSource.Body.Type;
            else if (pm.HasConstant) sourceType = pm.ConstantType ?? pm.DestinationType;
            else if (pm.SourceChain is { Count: > 0 } chain) sourceType = MemberMatcher.MemberType(chain[^1]);
            else continue;

            if (!IsLeanConversion(sourceType, pm.DestinationType, path)) return false;
        }
        return true;
    }

    /// <summary>Mirrors <see cref="ExpressionBuilder.Convert"/>: true when that conversion never touches the context.</summary>
    private bool IsLeanConversion(Type s, Type d, HashSet<int> path)
    {
        var tm = FindTypeMap(s, d);
        if (tm != null) return IsLeanMap(tm, path);

        if (s == d && TypeHelpers.IsLeaf(s)) return true;

        var sNull = Nullable.GetUnderlyingType(s);
        var dNull = Nullable.GetUnderlyingType(d);
        if (sNull != null) return IsLeanConversion(sNull, dNull ?? d, path);
        if (dNull != null) return s == typeof(string) || IsLeanConversion(s, dNull, path);

        // property bags create instances through the context
        if (TypeHelpers.TryGetKeyValueElement(s, out var bagKey, out _) && bagKey == typeof(string) &&
            !TypeHelpers.IsBuildableDictionary(d, out _, out _) && ExpressionBuilder.IsPlainObject(d)) return false;
        if (TypeHelpers.IsBuildableDictionary(d, out var toKey, out _) && toKey == typeof(string) &&
            !TypeHelpers.TryGetEnumerableElement(s, out _) && ExpressionBuilder.IsPlainObject(s)) return false;

        // a type can be a collection of itself (e.g. JToken : IEnumerable<JToken>): a repeated pair is a cycle, so not lean
        var pairKey = -((HashCode.Combine(s, d) & 0x7fffffff) + 1);   // negative: never collides with a type map id
        if (TypeHelpers.IsBuildableDictionary(d, out var kd, out var vd) && TypeHelpers.TryGetKeyValueElement(s, out var ks, out var vs))
        {
            if (!path.Add(pairKey)) return false;
            try { return IsLeanElement(ks, kd, path) && IsLeanElement(vs, vd, path); }
            finally { path.Remove(pairKey); }
        }

        if (TypeHelpers.TryGetEnumerableElement(d, out var de) && TypeHelpers.TryGetEnumerableElement(s, out var se))
        {
            if (!path.Add(pairKey)) return false;
            try { return IsLeanElement(se, de, path); }
            finally { path.Remove(pairKey); }
        }

        if (d.IsAssignableFrom(s)) return true;
        if (d == typeof(string) || s == typeof(string)) return true;
        if (s.IsEnum || d.IsEnum) return true;
        if (TypeHelpers.IsNumericLike(s) && TypeHelpers.IsNumericLike(d)) return true;
        if (TypeHelpers.IsConvertiblePrimitive(s) && TypeHelpers.IsConvertiblePrimitive(d)) return true;
        return false;   // missing map, user-defined operators, ...: keep the general path
    }

    private bool IsLeanElement(Type s, Type d, HashSet<int> path) =>
        s == d && TypeHelpers.IsImmutableLeaf(s) || IsLeanConversion(s, d, path);

    // ------------------------------------------------------------------ lookup

    /// <summary>Exact lookup; closes an open generic map on demand.</summary>
    internal TypeMap? TryGetTypeMap(Type source, Type destination)
    {
        var pair = new TypePair(source, destination);
        if (_typeMaps.TryGetValue(pair, out var exact)) return exact;
        return _openMaps.Count == 0 ? null : TryCreateFromOpenGeneric(pair);
    }

    /// <summary>Exact map, else the map of a base class / implemented interface of the source.</summary>
    internal TypeMap? FindTypeMap(Type source, Type destination)
    {
        var exact = TryGetTypeMap(source, destination);
        if (exact != null) return exact;
        if (source.IsValueType) return null;

        for (var b = source.BaseType; b != null && b != typeof(object); b = b.BaseType)
            if (TryGetTypeMap(b, destination) is { } inherited) return inherited;

        foreach (var i in source.GetInterfaces())
            if (_typeMaps.TryGetValue(new TypePair(i, destination), out var viaInterface)) return viaInterface;
        return null;
    }

    private TypeMap? TryCreateFromOpenGeneric(TypePair pair)
    {
        if (!pair.Source.IsGenericType || !pair.Destination.IsGenericType) return null;
        var sourceDefinition = pair.Source.GetGenericTypeDefinition();
        var destinationDefinition = pair.Destination.GetGenericTypeDefinition();

        var open = _openMaps.FirstOrDefault(o => o.Source == sourceDefinition && o.Destination == destinationDefinition);
        if (open == null) return null;

        lock (_createLock)
        {
            if (_typeMaps.TryGetValue(pair, out var existing)) return existing;

            var typeMap = new TypeMap(pair.Source, pair.Destination, open.Profile)
            {
                MemberList = open.MemberList,
                IncludeAllDerived = open.IncludeAllDerived,
                PreserveReferences = open.PreserveReferences,
                MaxDepth = open.MaxDepth,
                IgnoreUnmapped = open.IgnoreUnmapped,
                Settings = _settings[open.Profile]
            };
            if (open.ConverterType != null)
                typeMap.ConverterType = CloseConverter(open.ConverterType, pair);

            _typeMaps[pair] = typeMap;
            Finish(typeMap);
            return typeMap;
        }
    }

    internal TypeMap? FindTypeMapById(int id) => _typeMaps.Values.FirstOrDefault(t => t.Id == id);

    private static Type CloseConverter(Type converter, TypePair pair)
    {
        if (!converter.IsGenericTypeDefinition) return converter;

        var sourceArguments = pair.Source.GetGenericArguments();
        var destinationArguments = pair.Destination.GetGenericArguments();
        var arity = converter.GetGenericArguments().Length;

        if (arity == sourceArguments.Length + destinationArguments.Length)
            return converter.MakeGenericType(sourceArguments.Concat(destinationArguments).ToArray());
        if (arity == sourceArguments.Length)
            return converter.MakeGenericType(sourceArguments);
        throw new MappingConfigurationException(
            $"Open generic converter {converter.Name} must take the source type arguments, or the source followed by the destination type arguments.");
    }

    internal TypeMap GetOrCreateTypeMap(Type source, Type destination)
    {
        lock (_createLock)
        {
            if (_typeMaps.TryGetValue(new TypePair(source, destination), out var existing)) return existing;
            var created = new TypeMap(source, destination, Options) { Settings = _rootSettings, IsInline = true };
            _typeMaps[created.Pair] = created;
            Finish(created);
            return created;
        }
    }

    // Hot path of Mapper.Map<TSource, TDestination>: a process-wide id per generic pair indexes a per-configuration array,
    // which is much cheaper than hashing a TypePair on every call.
    private static class PairId<TSource, TDestination>
    {
        public static readonly int Value = Interlocked.Increment(ref _pairCounter);
    }

    private static int _pairCounter = -1;
    private volatile MappingBase?[] _fast = new MappingBase?[16];

    internal Mapping<TSource, TDestination> GetMapping<TSource, TDestination>()
    {
        var id = PairId<TSource, TDestination>.Value;
        var table = _fast;
        if ((uint)id < (uint)table.Length && table[id] is { } hit)
            return System.Runtime.CompilerServices.Unsafe.As<Mapping<TSource, TDestination>>(hit);
        return GetMappingSlow<TSource, TDestination>(id);
    }

    // Lean delegates are cached next to the holders so the hot call is: static id -> array slot -> delegate invoke.
    private volatile object?[] _leanFast = new object?[16];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal Func<TSource, TDestination, TDestination>? TryGetLean<TSource, TDestination>()
    {
        var id = PairId<TSource, TDestination>.Value;
        var table = _leanFast;
        return (uint)id < (uint)table.Length && table[id] is { } hit
            ? System.Runtime.CompilerServices.Unsafe.As<Func<TSource, TDestination, TDestination>>(hit)
            : null;
    }

    /// <summary>Resolves the map once; returns its lean delegate (and caches it) or null when the map needs a context.</summary>
    internal Func<TSource, TDestination, TDestination>? ResolveLean<TSource, TDestination>()
    {
        var mapping = GetMapping<TSource, TDestination>();
        if (!mapping.IsLean) return null;
        var lean = mapping.LeanDelegate;
        var id = PairId<TSource, TDestination>.Value;
        lock (_createLock)
        {
            var table = _leanFast;
            if (id >= table.Length)
            {
                var grown = new object?[Math.Max(id + 1, table.Length * 2)];
                Array.Copy(table, grown, table.Length);
                table = grown;
                _leanFast = table;
            }
            table[id] = lean;
        }
        return lean;
    }

    private Mapping<TSource, TDestination> GetMappingSlow<TSource, TDestination>(int id)
    {
        var mapping = (Mapping<TSource, TDestination>)GetMapping(typeof(TSource), typeof(TDestination));
        lock (_createLock)
        {
            var table = _fast;
            if (id >= table.Length)
            {
                var grown = new MappingBase?[Math.Max(id + 1, table.Length * 2)];
                Array.Copy(table, grown, table.Length);
                table = grown;
                _fast = table;
            }
            table[id] = mapping;
        }
        return mapping;
    }

    private sealed class TypeMapInfo : ITypeMapInfo
    {
        private readonly TypeMap _map;
        public TypeMapInfo(TypeMap map) => _map = map;
        public Type SourceType => _map.SourceType;
        public Type DestinationType => _map.DestinationType;
        public MemberList MemberList => _map.MemberList;
    }

    internal MappingBase GetMapping(Type source, Type destination) =>
        _mappings.GetOrAdd(new TypePair(source, destination), static (pair, self) =>
        {
            var typeMap = self.TryGetTypeMap(pair.Source, pair.Destination);
            var type = typeof(Mapping<,>).MakeGenericType(pair.Source, pair.Destination);
            return (MappingBase)Activator.CreateInstance(
                type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object?[] { self, typeMap }, null)!;
        }, this);

    internal LambdaExpression GetProjection(Type source, Type destination, ISet<string>? membersToExpand = null)
    {
        if (membersToExpand is { Count: > 0 })
            return new ProjectionBuilder(this).Build(source, destination, membersToExpand);

        return _projections.GetOrAdd(new TypePair(source, destination),
            static (pair, self) => new ProjectionBuilder(self).Build(pair.Source, pair.Destination, null), this);
    }

    // ------------------------------------------------------------------ inheritance

    private void LinkInheritance()
    {
        foreach (var typeMap in _typeMaps.Values.ToList())
        {
            foreach (var pair in typeMap.Includes)
            {
                if (!_typeMaps.TryGetValue(pair, out var derived))
                    throw new MappingConfigurationException($"{typeMap.Pair} includes {pair}, but that map does not exist.");
                Link(typeMap, derived);
            }
            foreach (var pair in typeMap.IncludeBases)
            {
                if (!_typeMaps.TryGetValue(pair, out var baseMap))
                    throw new MappingConfigurationException($"{typeMap.Pair} inherits from {pair}, but that map does not exist.");
                Link(baseMap, typeMap);
            }
            if (typeMap.IncludeAllDerived)
            {
                foreach (var candidate in _typeMaps.Values.ToList())
                {
                    if (ReferenceEquals(candidate, typeMap)) continue;
                    if (typeMap.SourceType.IsAssignableFrom(candidate.SourceType) && typeMap.DestinationType.IsAssignableFrom(candidate.DestinationType))
                        Link(typeMap, candidate);
                }
            }
        }
    }

    private static void Link(TypeMap baseMap, TypeMap derived)
    {
        if (!baseMap.DerivedTypeMaps.Contains(derived)) baseMap.DerivedTypeMaps.Add(derived);
        if (!derived.BaseMaps.Contains(baseMap)) derived.BaseMaps.Add(baseMap);
    }

    private static void Inherit(TypeMap derived, TypeMap baseMap)
    {
        foreach (var (name, map) in baseMap.Overrides)
            if (!derived.Overrides.ContainsKey(name)) derived.Overrides[name] = map.Clone();
        foreach (var (name, map) in baseMap.CtorParamOverrides)
            if (!derived.CtorParamOverrides.ContainsKey(name)) derived.CtorParamOverrides[name] = map.Clone();

        derived.ForAllMembers.InsertRange(0, baseMap.ForAllMembers);
        derived.ForAllOtherMembers.InsertRange(0, baseMap.ForAllOtherMembers);
        derived.BeforeMaps.InsertRange(0, baseMap.BeforeMaps);
        derived.AfterMaps.InsertRange(0, baseMap.AfterMaps);
        derived.IncludedMembers.AddRange(baseMap.IncludedMembers);
        derived.ValueTransformers.AddRange(baseMap.ValueTransformers);
        foreach (var name in baseMap.SourceMembersNotValidated) derived.SourceMembersNotValidated.Add(name);
        derived.PreserveReferences |= baseMap.PreserveReferences;
        derived.IgnoreUnmapped |= baseMap.IgnoreUnmapped;
        derived.DisableCtorValidation |= baseMap.DisableCtorValidation;
        if (derived.MaxDepth == 0) derived.MaxDepth = baseMap.MaxDepth;
    }

    // ------------------------------------------------------------------ resolving members

    private void Finish(TypeMap typeMap)
    {
        if (typeMap.Finished) return;
        typeMap.Finished = true;

        foreach (var baseMap in typeMap.BaseMaps.ToList())
        {
            Finish(baseMap);
            Inherit(typeMap, baseMap);
        }
        if (typeMap.HasCustomConversion) return;

        var matcher = typeMap.Settings.Matcher;

        ResolveConstructor(typeMap);
        var consumedByCtor = typeMap.CtorParams
            .Select(p => matcher.Normalize(p.DestinationName))
            .ToHashSet(StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in matcher.GetDestinationMembers(typeMap.DestinationType))
        {
            seen.Add(m.Name);
            if (consumedByCtor.Contains(matcher.Normalize(m.Name))) continue;

            var hasOverride = typeMap.Overrides.TryGetValue(m.Name, out var propertyMap);
            if (!hasOverride)
            {
                var globallyIgnored = typeMap.Settings.GlobalIgnores.Any(g => m.Name.StartsWith(g, StringComparison.Ordinal));
                propertyMap = new PropertyMap(m.Name, m.Type, m.Member) { Ignored = m.IgnoreAttribute || globallyIgnored };
            }
            propertyMap!.IsGetOnlyCollection = m.GetOnlyCollection;

            if (!propertyMap.IsResolved) AssignConventionalSource(typeMap, propertyMap);
            typeMap.PropertyMaps.Add(propertyMap);
            foreach (var action in typeMap.ForAllMembers) action(propertyMap);
            if (!hasOverride)
                foreach (var action in typeMap.ForAllOtherMembers) action(propertyMap);
        }

        foreach (var (key, map) in typeMap.Overrides)
        {
            if (map.DestinationPath != null)
            {
                typeMap.PropertyMaps.Add(map);   // ForPath
                continue;
            }
            if (!seen.Contains(key) && !consumedByCtor.Contains(matcher.Normalize(key)))
                throw new MappingConfigurationException(
                    $"{typeMap.Pair}: ForMember targets '{key}', which is not a public writable member of {TypeHelpers.Pretty(typeMap.DestinationType)}.");
        }

        if (typeMap.ReverseOf != null) AddUnflattenedMembers(typeMap);
        if (typeMap.Settings is { } && Options.MlMatching.Enabled) ApplyMlMatching(typeMap);

        // stable: declaration order within an Order value
        var ordered = typeMap.PropertyMaps.OrderBy(p => p.Order).ToList();
        typeMap.PropertyMaps.Clear();
        typeMap.PropertyMaps.AddRange(ordered);
    }

    /// <summary>Source for a destination member by convention: the source itself first, then IncludeMembers.</summary>
    private static void AssignConventionalSource(TypeMap typeMap, PropertyMap propertyMap)
    {
        var (chain, custom) = ResolveSource(typeMap, propertyMap.DestinationName);
        if (chain != null) propertyMap.SourceChain = chain;
        else if (custom != null) propertyMap.CustomSource = custom;
    }

    private static (IReadOnlyList<MemberInfo>? Chain, LambdaExpression? Custom) ResolveSource(TypeMap typeMap, string name)
    {
        var matcher = typeMap.Settings.Matcher;
        var chain = matcher.Resolve(name, typeMap.SourceType);
        if (chain != null) return (chain, null);

        foreach (var included in typeMap.IncludedMembers)
        {
            var includedChain = matcher.Resolve(name, included.Body.Type);
            if (includedChain == null) continue;

            Expression body = included.Body;
            foreach (var member in includedChain)
                body = member is MethodInfo method ? Expression.Call(body, method) : Expression.MakeMemberAccess(body, member);
            return (null, Expression.Lambda(body, included.Parameters));
        }
        return (null, null);
    }

    /// <summary>ReverseMap of conventionally flattened maps: <c>AddressCity</c> to <c>Address.City</c>.</summary>
    private void AddUnflattenedMembers(TypeMap reverse)
    {
        var forward = reverse.ReverseOf!;
        Finish(forward);

        foreach (var map in forward.PropertyMaps)
        {
            if (map.Ignored || map.DestinationMember == null || map.DestinationPath != null) continue;
            if (map.SourceChain is not { Count: > 1 } chain || map.FromMl) continue;
            if (chain.Any(m => m is MethodInfo)) continue;

            var leaf = chain[^1];
            if (!(leaf is FieldInfo { IsInitOnly: false } || leaf is PropertyInfo { SetMethod.IsPublic: true })) continue;

            var key = string.Join('.', chain.Select(m => m.Name));
            if (reverse.Overrides.ContainsKey(key) || reverse.PropertyMaps.Any(p => p.DestinationPath != null && PathKey(p) == key)) continue;

            var parameter = Expression.Parameter(reverse.SourceType, "d");
            reverse.PropertyMaps.Add(new PropertyMap(leaf.Name, ConfigHelpers.TypeOf(leaf), leaf)
            {
                DestinationPath = chain.Take(chain.Count - 1).ToList(),
                CustomSource = Expression.Lambda(Expression.MakeMemberAccess(parameter, map.DestinationMember), parameter)
            });
        }

        static string PathKey(PropertyMap p) => string.Join('.', p.DestinationPath!.Select(m => m.Name).Append(p.DestinationName));
    }

    private void ApplyMlMatching(TypeMap typeMap)
    {
        var unmatched = typeMap.PropertyMaps.Where(p => !p.IsResolved && p.DestinationPath == null).ToList();
        if (unmatched.Count == 0) return;

        var candidates = typeMap.Settings.Matcher.GetCandidateMembers(typeMap.SourceType).ToList();
        if (candidates.Count == 0) return;

        var suggestions = _ml.Value.Match(
            unmatched.Select(p => p.DestinationName).ToList(),
            candidates.Select(c => c.Name).ToList(),
            Options.MlMatching);

        foreach (var property in unmatched)
        {
            if (!suggestions.TryGetValue(property.DestinationName, out var suggestion)) continue;
            var member = candidates.First(c => c.Name == suggestion.SourceName).Member;
            var sourceType = MemberMatcher.MemberType(member);

            // never let a name guess turn a scalar into a collection/complex type or vice versa
            if (TypeHelpers.IsCollectionLike(sourceType) != TypeHelpers.IsCollectionLike(property.DestinationType)) continue;
            if (TypeHelpers.IsLeaf(sourceType) != TypeHelpers.IsLeaf(property.DestinationType)) continue;

            property.SourceChain = new[] { member };
            property.FromMl = true;
            property.MlScore = suggestion.Score;
        }
    }

    private static void ResolveConstructor(TypeMap typeMap)
    {
        if (typeMap.ConstructUsing != null || typeMap.ConstructUsingServiceLocator) return;
        var d = typeMap.DestinationType;
        var matcher = typeMap.Settings.Matcher;

        var constructors = d.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => typeMap.Settings.ShouldUseConstructor(c))
            .OrderByDescending(c => c.GetParameters().Length)
            .ToList();

        foreach (var ctor in constructors)
        {
            var parameters = ctor.GetParameters();
            if (parameters.Any(p => p.ParameterType == d || p.ParameterType.IsByRef || p.ParameterType.IsPointer)) continue;

            var maps = new List<PropertyMap>();
            var ok = true;
            foreach (var p in parameters)
            {
                var map = new PropertyMap(p.Name!, p.ParameterType, null);

                if (typeMap.CtorParamOverrides.TryGetValue(p.Name!, out var ctorOverride) &&
                    (ctorOverride.CustomSource != null || ctorOverride.Resolver != null))
                {
                    CopySource(ctorOverride, map);
                }
                else if (typeMap.Overrides.Values.FirstOrDefault(o =>
                             matcher.Normalize(o.DestinationName) == matcher.Normalize(p.Name!) && !o.Ignored && o.DestinationPath == null) is { } memberOverride &&
                         (memberOverride.CustomSource != null || memberOverride.Resolver != null || memberOverride.HasConstant))
                {
                    CopySource(memberOverride, map);
                }
                else
                {
                    var (chain, custom) = ResolveSource(typeMap, p.Name!);
                    if (chain != null) map.SourceChain = chain;
                    else if (custom != null) map.CustomSource = custom;
                    else if (p.HasDefaultValue)
                    {
                        map.HasConstant = true;
                        var value = p.DefaultValue;
                        map.Constant = value is DBNull or null
                            ? (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                            : value;
                        map.ConstantType = p.ParameterType;
                    }
                    else
                    {
                        ok = false;
                        break;
                    }
                }
                maps.Add(map);
            }

            if (!ok) continue;
            typeMap.Constructor = ctor;
            typeMap.CtorParams.AddRange(maps);
            return;
        }

        if (!d.IsValueType)
        {
            typeMap.ConstructorError = d.IsAbstract || d.IsInterface
                ? $"{TypeHelpers.Pretty(d)} is abstract/an interface; use ConstructUsing(), As<>() or Include<>() a concrete derived map."
                : $"No public constructor of {TypeHelpers.Pretty(d)} can be satisfied from {TypeHelpers.Pretty(typeMap.SourceType)}. " +
                  "Add a parameterless constructor, name constructor parameters after source members, or use ConstructUsing()/ForCtorParam().";
        }

        static void CopySource(PropertyMap from, PropertyMap to)
        {
            to.CustomSource = from.CustomSource;
            to.Resolver = from.Resolver;
            to.ResolverKind = from.ResolverKind;
            to.ResolverSource = from.ResolverSource;
            to.HasConstant = from.HasConstant;
            to.Constant = from.Constant;
            to.ConstantType = from.ConstantType;
            to.HasNullSubstitute = from.HasNullSubstitute;
            to.NullSubstitute = from.NullSubstitute;
            to.AllowNull = from.AllowNull;
            to.MapAtRuntime = from.MapAtRuntime;
        }
    }

    // ------------------------------------------------------------------ validation / diagnostics

    public void AssertConfigurationIsValid() => Validate(null);

    public void AssertConfigurationIsValid(string profileName)
    {
        ArgumentNullException.ThrowIfNull(profileName);
        if (!_settings.Keys.Any(p => p.ProfileName == profileName))
            throw new MappingConfigurationException($"No profile named '{profileName}'.");
        Validate(profileName);
    }

    public void AssertConfigurationIsValid<TProfile>() where TProfile : Profile
    {
        var profile = _settings.Keys.FirstOrDefault(p => p is TProfile)
                      ?? throw new MappingConfigurationException($"Profile {typeof(TProfile).Name} is not part of this configuration.");
        Validate(profile.ProfileName);
    }

    private void Validate(string? profileName)
    {
        var errors = new List<string>();

        // compiling a map can create inline maps (CreateMissingTypeMaps); do that first so they are validated too
        for (var known = -1; known != _typeMaps.Count;)
        {
            known = _typeMaps.Count;
            foreach (var pending in _typeMaps.Values.ToList())
            {
                if (pending.ProjectionOnly) continue;
                try { GetMapping(pending.SourceType, pending.DestinationType).EnsureCompiled(); }
                catch (Exception) { /* reported by the validation pass below */ }
            }
        }

        foreach (var typeMap in _typeMaps.Values.OrderBy(t => t.Pair.ToString(), StringComparer.Ordinal))
        {
            if (profileName != null && typeMap.Profile.ProfileName != profileName) continue;
            if (typeMap.IsInline && !Options.ValidateInlineMaps) continue;

            if (Options.EnumMappingValidation && typeMap.EnumUnmapped.Count > 0)
                errors.Add($"{typeMap.Pair}: enum values without a mapping: {string.Join(", ", typeMap.EnumUnmapped)}. Use MapValue() or MapByValue().");

            ValidateMembers(typeMap, errors);
            try
            {
                if (typeMap.ProjectionOnly) GetProjection(typeMap.SourceType, typeMap.DestinationType);   // projection maps are checked by building the projection
                else GetMapping(typeMap.SourceType, typeMap.DestinationType).EnsureCompiled();
            }
            catch (Exception ex)
            {
                errors.Add($"{typeMap.Pair}: {(ex.InnerException is AutoMappingException inner ? inner.Message : ex.Message)}");
            }
        }
        if (errors.Count > 0) throw new ConfigurationValidationException(errors);
    }

    private static void ValidateMembers(TypeMap typeMap, List<string> errors)
    {
        if (typeMap.HasCustomConversion || typeMap.MemberList == MemberList.None) return;

        if (typeMap.ConstructorError != null && !typeMap.DisableCtorValidation && typeMap.ConstructUsing == null &&
            !typeMap.ConstructUsingServiceLocator &&
            !((typeMap.DestinationType.IsAbstract || typeMap.DestinationType.IsInterface) && typeMap.DerivedTypeMaps.Count > 0))
            errors.Add($"{typeMap.Pair}: {typeMap.ConstructorError}");

        if (typeMap.MemberList == MemberList.Destination)
        {
            if (typeMap.IgnoreUnmapped) return;
            foreach (var unmapped in typeMap.Unmapped)
                errors.Add($"{typeMap.Pair}: destination member '{unmapped.DestinationName}' has no matching source member. " +
                           "Map it with ForMember(), mark it [IgnoreMap], or call IgnoreUnmapped().");
            return;
        }

        // MemberList.Source: every source property/field must feed some destination member
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in typeMap.PropertyMaps.Concat(typeMap.CtorParams))
        {
            if (map.SourceChain is { Count: > 0 } chain) consumed.Add(chain[0].Name);
            if (map.CustomSource != null) FirstLevelMembers.Collect(map.CustomSource, consumed);
            if (map.ResolverSource != null) FirstLevelMembers.Collect(map.ResolverSource, consumed);
        }
        foreach (var included in typeMap.IncludedMembers) FirstLevelMembers.Collect(included, consumed);

        foreach (var member in typeMap.Settings.Matcher.GetSourceMemberList(typeMap.SourceType).OfType<MemberInfo>()
                     .Where(m => m is PropertyInfo or FieldInfo))
        {
            if (consumed.Contains(member.Name) || typeMap.SourceMembersNotValidated.Contains(member.Name)) continue;
            errors.Add($"{typeMap.Pair}: source member '{member.Name}' is not mapped. " +
                       "Map it, or call ForSourceMember(..., o => o.DoNotValidate()).");
        }
    }

    private sealed class FirstLevelMembers : ExpressionVisitor
    {
        private readonly ParameterExpression _root;
        private readonly HashSet<string> _found;

        private FirstLevelMembers(ParameterExpression root, HashSet<string> found)
        {
            _root = root;
            _found = found;
        }

        public static void Collect(LambdaExpression lambda, HashSet<string> found) =>
            new FirstLevelMembers(lambda.Parameters[0], found).Visit(lambda.Body);

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == _root) _found.Add(node.Member.Name);
            return base.VisitMember(node);
        }
    }

    /// <summary>Compiles every configured map now (startup warm-up) instead of on first use.</summary>
    public void CompileMappings()
    {
        foreach (var typeMap in _typeMaps.Values.ToList())
            if (!typeMap.ProjectionOnly) GetMapping(typeMap.SourceType, typeMap.DestinationType).EnsureCompiled();
    }

    /// <summary>True when some map with this destination type declares an <c>EqualityComparison</c> (collection equivalency).</summary>
    private volatile IMapperObserver? _observer;

    /// <summary>Optional metrics sink; set once at startup (e.g. by the Reporting feature). Null by default.</summary>
    public IMapperObserver? Observer
    {
        get => _observer;
        set => _observer = value;
    }

    /// <summary>Snapshot of the internal cache sizes (cheap; safe to call from a timer).</summary>
    public MapperCacheStatistics GetCacheStatistics() =>
        new(_typeMaps.Count, _mappings.Count, _projections.Count, _leanMaps.Count, _fast.Count(slot => slot != null));

    internal void NotifyCompiled(Type source, Type destination, TimeSpan duration, bool lean)
    {
        var observer = _observer;
        if (observer == null) return;
        try { observer.OnCompiled(source, destination, duration, lean); }
        catch { /* diagnostics must never break mapping */ }
    }

    internal LambdaExpression? GetEqualityExpression(Type source, Type destination) => FindTypeMap(source, destination)?.EqualityExpression;

    internal bool HasEquivalencyFor(Type destinationElement) =>
        _typeMaps.Values.Any(t => t.EqualityComparison != null && t.DestinationType == destinationElement);

    public LambdaExpression BuildExecutionPlan(Type sourceType, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);
        var typeMap = TryGetTypeMap(sourceType, destinationType);
        var builder = CreateBuilder(typeMap);
        return typeMap != null ? builder.BuildTypeMapLambda(typeMap) : builder.BuildConversionLambda(sourceType, destinationType);
    }

    public string GetMappingPlan<TSource, TDestination>() => GetMappingPlan(typeof(TSource), typeof(TDestination));

    /// <summary>Human readable description of how each destination member is filled, including ML.NET matches.</summary>
    public string GetMappingPlan(Type sourceType, Type destinationType)
    {
        var typeMap = TryGetTypeMap(sourceType, destinationType)
                      ?? throw new MappingConfigurationException($"No map from {TypeHelpers.Pretty(sourceType)} to {TypeHelpers.Pretty(destinationType)}.");

        var sb = new StringBuilder();
        sb.AppendLine(typeMap.Pair.ToString());
        if (typeMap.CustomConverter != null || typeMap.ConverterType != null)
        {
            sb.AppendLine("  (custom ConvertUsing)");
            return sb.ToString();
        }
        if (typeMap.AsType != null)
        {
            sb.AppendLine($"  (maps as {TypeHelpers.Pretty(typeMap.AsType)})");
            return sb.ToString();
        }

        if (typeMap.ConstructUsing != null) sb.AppendLine("  ctor: ConstructUsing");
        else if (typeMap.ConstructUsingServiceLocator) sb.AppendLine("  ctor: service locator");
        else if (typeMap.Constructor != null)
            sb.AppendLine($"  ctor({string.Join(", ", typeMap.CtorParams.Select(p => $"{p.DestinationName} <- {Describe(p)}"))})");

        foreach (var p in typeMap.PropertyMaps)
        {
            var target = p.DestinationPath == null ? p.DestinationName : string.Join('.', p.DestinationPath.Select(m => m.Name).Append(p.DestinationName));
            sb.AppendLine($"  {target} <- {Describe(p)}");
        }
        return sb.ToString();

        static string Describe(PropertyMap p)
        {
            if (p.Ignored) return "(ignored)";
            if (p.CustomSource != null) return $"expression {p.CustomSource.Body}";
            if (p.Resolver != null) return p.ResolverKind switch
            {
                ResolverKind.Converter => "value converter",
                ResolverKind.SourceMember => "member value resolver",
                _ => "value resolver"
            };
            if (p.HasConstant) return $"constant {p.Constant ?? "null"}";
            if (p.SourceChain != null)
            {
                var path = string.Join('.', p.SourceChain.Select(m => m.Name));
                return p.FromMl ? $"{path}  [ML.NET fuzzy match, similarity {p.MlScore:0.00}]" : path;
            }
            return "UNMAPPED";
        }
    }
}
