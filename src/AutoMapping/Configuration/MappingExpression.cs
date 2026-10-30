using System.Linq.Expressions;
using System.Reflection;
using AutoMapping.Internal;

namespace AutoMapping;

internal sealed class MappingExpression<TSource, TDestination> : IMappingExpression<TSource, TDestination>
{
    private readonly TypeMap _typeMap;
    private readonly Profile _profile;

    public MappingExpression(TypeMap typeMap, Profile profile)
    {
        _typeMap = typeMap;
        _profile = profile;
    }

    // ------------------------------------------------------------------ members

    public IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> options)
    {
        ArgumentNullException.ThrowIfNull(destinationMember);
        ArgumentNullException.ThrowIfNull(options);

        var chain = ConfigHelpers.MemberChain(destinationMember.Body)
                    ?? throw new ArgumentException("ForMember needs a member of the destination, e.g. d => d.Name.", nameof(destinationMember));
        if (chain.Count > 1)
            throw new ArgumentException("ForMember cannot target nested members (d => d.A.B). Use ForPath for that.", nameof(destinationMember));

        var member = chain[0];
        options(new MemberConfigurationExpression<TSource, TDestination, TMember>(GetOrAddOverride(member.Name, member, null)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForMember(
        string name, Action<IMemberConfigurationExpression<TSource, TDestination, object>> options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);

        var member = typeof(TDestination).GetMember(name, BindingFlags.Public | BindingFlags.Instance)
                         .FirstOrDefault(m => m is PropertyInfo or FieldInfo)
                     ?? throw new MappingConfigurationException($"'{name}' is not a member of {TypeHelpers.Pretty(typeof(TDestination))}.");
        options(new MemberConfigurationExpression<TSource, TDestination, object>(GetOrAddOverride(member.Name, member, null)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForPath<TMember>(
        Expression<Func<TDestination, TMember>> destinationPath,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> options)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(options);

        var chain = ConfigHelpers.MemberChain(destinationPath.Body)
                    ?? throw new ArgumentException("ForPath needs a member path of the destination, e.g. d => d.Address.City.", nameof(destinationPath));
        var leaf = chain[^1];
        var parents = chain.Count > 1 ? chain.Take(chain.Count - 1).ToList() : null;
        var key = string.Join('.', chain.Select(m => m.Name));

        options(new MemberConfigurationExpression<TSource, TDestination, TMember>(GetOrAddOverride(key, leaf, parents)));
        return this;
    }

    private PropertyMap GetOrAddOverride(string key, MemberInfo leaf, IReadOnlyList<MemberInfo>? parents)
    {
        if (!_typeMap.Overrides.TryGetValue(key, out var map))
        {
            map = new PropertyMap(leaf.Name, ConfigHelpers.TypeOf(leaf), leaf) { DestinationPath = parents };
            _typeMap.Overrides[key] = map;
        }
        return map;
    }

    public IMappingExpression<TSource, TDestination> ForAllMembers(Action<IMemberConfigurationExpression<TSource, TDestination, object>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _typeMap.ForAllMembers.Add(pm => options(new MemberConfigurationExpression<TSource, TDestination, object>(pm)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForAllOtherMembers(Action<IMemberConfigurationExpression<TSource, TDestination, object>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _typeMap.ForAllOtherMembers.Add(pm => options(new MemberConfigurationExpression<TSource, TDestination, object>(pm)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForCtorParam(string ctorParamName, Action<ICtorParamConfigurationExpression<TSource>> options)
    {
        ArgumentException.ThrowIfNullOrEmpty(ctorParamName);
        ArgumentNullException.ThrowIfNull(options);

        if (!_typeMap.CtorParamOverrides.TryGetValue(ctorParamName, out var map))
        {
            map = new PropertyMap(ctorParamName, typeof(object), null);
            _typeMap.CtorParamOverrides[ctorParamName] = map;
        }
        options(new CtorParamConfigurationExpression<TSource, TDestination>(map));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ForSourceMember<TSourceMember>(
        Expression<Func<TSource, TSourceMember>> sourceMember, Action<ISourceMemberConfigurationExpression> options)
    {
        ArgumentNullException.ThrowIfNull(sourceMember);
        var chain = ConfigHelpers.MemberChain(sourceMember.Body)
                    ?? throw new ArgumentException("ForSourceMember needs a source member, e.g. s => s.Name.", nameof(sourceMember));
        return ForSourceMember(chain[0].Name, options);
    }

    public IMappingExpression<TSource, TDestination> ForSourceMember(string sourceMemberName, Action<ISourceMemberConfigurationExpression> options)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceMemberName);
        ArgumentNullException.ThrowIfNull(options);
        options(new SourceMemberConfigurationExpression(() => _typeMap.SourceMembersNotValidated.Add(sourceMemberName)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> IncludeMembers(params Expression<Func<TSource, object>>[] memberExpressions)
    {
        ArgumentNullException.ThrowIfNull(memberExpressions);
        foreach (var expression in memberExpressions)
            _typeMap.IncludedMembers.Add(Expression.Lambda(ConfigHelpers.StripConvert(expression.Body), expression.Parameters));
        return this;
    }

    // ------------------------------------------------------------------ hooks / construction / conversion

    public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _typeMap.BeforeMaps.Add(new Action<TSource, TDestination, ResolutionContext>((s, d, _) => action(s, d)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination, ResolutionContext> action)
    {
        _typeMap.BeforeMaps.Add(action ?? throw new ArgumentNullException(nameof(action)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> BeforeMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>
    {
        _typeMap.BeforeMaps.Add(new Action<TSource, TDestination, ResolutionContext>(
            (s, d, c) => ((IMappingAction<TSource, TDestination>)c.CreateInstance(typeof(TMappingAction))).Process(s, d, c)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _typeMap.AfterMaps.Add(new Action<TSource, TDestination, ResolutionContext>((s, d, _) => action(s, d)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, ResolutionContext> action)
    {
        _typeMap.AfterMaps.Add(action ?? throw new ArgumentNullException(nameof(action)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> AfterMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>
    {
        _typeMap.AfterMaps.Add(new Action<TSource, TDestination, ResolutionContext>(
            (s, d, c) => ((IMappingAction<TSource, TDestination>)c.CreateInstance(typeof(TMappingAction))).Process(s, d, c)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, TDestination> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _typeMap.ConstructUsing = new Func<TSource, ResolutionContext, TDestination>((s, _) => factory(s));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, ResolutionContext, TDestination> factory)
    {
        _typeMap.ConstructUsing = factory ?? throw new ArgumentNullException(nameof(factory));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConstructUsingServiceLocator()
    {
        _typeMap.ConstructUsingServiceLocator = true;
        return this;
    }

    internal TypeMap TypeMap => _typeMap;

    public IMappingExpression<TSource, TDestination> ConvertUsing(Expression<Func<TSource, TDestination>> mappingExpression)
    {
        ArgumentNullException.ThrowIfNull(mappingExpression);
        var converter = mappingExpression.Compile();
        _typeMap.ConvertExpression = mappingExpression;
        _typeMap.CustomConverter = new Func<TSource, TDestination, ResolutionContext, TDestination>((s, _, _) => converter(s));
        return this;
    }

    public IMappingExpression<TSource, TDestination> EqualityComparison(Expression<Func<TSource, TDestination, bool>> equalityComparison)
    {
        ArgumentNullException.ThrowIfNull(equalityComparison);
        _typeMap.EqualityComparison = equalityComparison.Compile();
        _typeMap.EqualityExpression = equalityComparison;
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConvertUsing(Func<TSource, TDestination, TDestination> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        _typeMap.CustomConverter = new Func<TSource, TDestination, ResolutionContext, TDestination>((s, d, _) => converter(s, d));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConvertUsing(Func<TSource, TDestination, ResolutionContext, TDestination> converter)
    {
        _typeMap.CustomConverter = converter ?? throw new ArgumentNullException(nameof(converter));
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConvertUsing(ITypeConverter<TSource, TDestination> converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        _typeMap.CustomConverter = new Func<TSource, TDestination, ResolutionContext, TDestination>(converter.Convert);
        return this;
    }

    public IMappingExpression<TSource, TDestination> ConvertUsing<TTypeConverter>() where TTypeConverter : ITypeConverter<TSource, TDestination>
    {
        _typeMap.ConverterType = typeof(TTypeConverter);
        return this;
    }

    // ------------------------------------------------------------------ reverse

    public IMappingExpression<TDestination, TSource> ReverseMap()
    {
        var reverse = new TypeMap(typeof(TDestination), typeof(TSource), _profile)
        {
            MemberList = _typeMap.MemberList,
            ReverseOf = _typeMap
        };
        _profile.TypeMaps.Add(reverse);

        // Explicit forward member maps such as ForMember(d => d.X, o => o.MapFrom(s => s.A.B)) become
        // ForPath(s => s.A.B) <- d.X in the reverse map. Conventional (flattened) maps are un-flattened when the map is finished.
        foreach (var forward in _typeMap.Overrides.Values)
        {
            if (forward.Ignored || forward.CustomSource == null || forward.DestinationMember == null || forward.DestinationPath != null) continue;

            var chain = ConfigHelpers.MemberChain(forward.CustomSource.Body);
            if (chain == null) continue;

            var leaf = chain[^1];
            if (!CanWrite(leaf)) continue;

            var parents = chain.Count > 1 ? chain.Take(chain.Count - 1).ToList() : null;
            var parameter = Expression.Parameter(typeof(TDestination), "d");
            var reversed = new PropertyMap(leaf.Name, ConfigHelpers.TypeOf(leaf), leaf)
            {
                DestinationPath = parents,
                CustomSource = Expression.Lambda(Expression.MakeMemberAccess(parameter, forward.DestinationMember), parameter)
            };
            reverse.Overrides[string.Join('.', chain.Select(m => m.Name))] = reversed;
        }
        return new MappingExpression<TDestination, TSource>(reverse, _profile);
    }

    private static bool CanWrite(MemberInfo member) =>
        member is FieldInfo { IsInitOnly: false } || member is PropertyInfo { SetMethod.IsPublic: true };

    // ------------------------------------------------------------------ inheritance and misc

    public IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
        where TDerivedSource : TSource where TDerivedDestination : TDestination
    {
        _typeMap.Includes.Add(new TypePair(typeof(TDerivedSource), typeof(TDerivedDestination)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> IncludeBase<TBaseSource, TBaseDestination>()
    {
        _typeMap.IncludeBases.Add(new TypePair(typeof(TBaseSource), typeof(TBaseDestination)));
        return this;
    }

    public IMappingExpression<TSource, TDestination> IncludeAllDerived()
    {
        _typeMap.IncludeAllDerived = true;
        return this;
    }

    public IMappingExpression<TSource, TDestination> As<TTarget>() where TTarget : TDestination
    {
        _typeMap.AsType = typeof(TTarget);
        return this;
    }

    public IMappingExpression<TSource, TDestination> PreserveReferences()
    {
        _typeMap.PreserveReferences = true;
        return this;
    }

    public IMappingExpression<TSource, TDestination> MaxDepth(int depth)
    {
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth), "MaxDepth must be at least 1.");
        _typeMap.MaxDepth = depth;
        return this;
    }

    public IMappingExpression<TSource, TDestination> DisableCtorValidation()
    {
        _typeMap.DisableCtorValidation = true;
        return this;
    }

    public IMappingExpression<TSource, TDestination> IgnoreUnmapped()
    {
        _typeMap.IgnoreUnmapped = true;
        return this;
    }

    public IMappingExpression<TSource, TDestination> ValidateMemberList(MemberList memberList)
    {
        _typeMap.MemberList = memberList;
        return this;
    }

    public IMappingExpression<TSource, TDestination> AddTransform<TValue>(Expression<Func<TValue, TValue>> transformer)
    {
        ArgumentNullException.ThrowIfNull(transformer);
        _typeMap.ValueTransformers.Add((typeof(TValue), transformer));
        return this;
    }
}

/// <summary>Configuration for maps created from runtime types (<c>CreateMap(typeof(A), typeof(B))</c>), including open generics.</summary>
internal sealed class NonGenericMappingExpression : IMappingExpression
{
    private readonly TypeMap? _typeMap;
    private readonly OpenGenericMap? _open;
    private readonly Profile _profile;

    public NonGenericMappingExpression(TypeMap? typeMap, OpenGenericMap? open, Profile profile)
    {
        _typeMap = typeMap;
        _open = open;
        _profile = profile;
    }

    private TypeMap Required() => _typeMap ?? throw new NotSupportedException("This configuration is not available on open generic maps.");

    public IMappingExpression ForAllMembers(Action<IMemberConfigurationExpression<object, object, object>> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Required().ForAllMembers.Add(pm => options(new MemberConfigurationExpression<object, object, object>(pm)));
        return this;
    }

    public IMappingExpression ForMember(string name, Action<IMemberConfigurationExpression<object, object, object>> options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        var map = Required();
        var member = map.DestinationType.GetMember(name, BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m is PropertyInfo or FieldInfo)
                     ?? throw new MappingConfigurationException($"'{name}' is not a member of {TypeHelpers.Pretty(map.DestinationType)}.");
        if (!map.Overrides.TryGetValue(member.Name, out var propertyMap))
            map.Overrides[member.Name] = propertyMap = new PropertyMap(member.Name, ConfigHelpers.TypeOf(member), member);
        options(new MemberConfigurationExpression<object, object, object>(propertyMap));
        return this;
    }

    public IMappingExpression EqualityComparison(LambdaExpression predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var map = Required();
        map.EqualityComparison = predicate.Compile();
        map.EqualityExpression = predicate;
        return this;
    }

    public IMappingExpression BeforeMap(Action<object, object> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Required().BeforeMaps.Add(new Action<object, object, ResolutionContext>((s, d, _) => action(s, d)));
        return this;
    }

    public IMappingExpression AfterMap(Action<object, object> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Required().AfterMaps.Add(new Action<object, object, ResolutionContext>((s, d, _) => action(s, d)));
        return this;
    }

    public IMappingExpression ConvertUsing(Type typeConverterType)
    {
        ArgumentNullException.ThrowIfNull(typeConverterType);
        if (_open != null) _open.ConverterType = typeConverterType;
        else _typeMap!.ConverterType = typeConverterType;
        return this;
    }

    public IMappingExpression IncludeAllDerived()
    {
        if (_open != null) _open.IncludeAllDerived = true;
        else _typeMap!.IncludeAllDerived = true;
        return this;
    }

    public IMappingExpression PreserveReferences()
    {
        if (_open != null) _open.PreserveReferences = true;
        else _typeMap!.PreserveReferences = true;
        return this;
    }

    public IMappingExpression MaxDepth(int depth)
    {
        if (depth < 1) throw new ArgumentOutOfRangeException(nameof(depth), "MaxDepth must be at least 1.");
        if (_open != null) _open.MaxDepth = depth;
        else _typeMap!.MaxDepth = depth;
        return this;
    }

    public IMappingExpression IgnoreUnmapped()
    {
        if (_open != null) _open.IgnoreUnmapped = true;
        else _typeMap!.IgnoreUnmapped = true;
        return this;
    }

    public IMappingExpression ReverseMap()
    {
        if (_open != null)
        {
            var reverse = new OpenGenericMap(_open.Destination, _open.Source, _profile) { MemberList = _open.MemberList };
            _profile.OpenGenericMaps.Add(reverse);
            return new NonGenericMappingExpression(null, reverse, _profile);
        }
        var typeMap = new TypeMap(_typeMap!.DestinationType, _typeMap.SourceType, _profile) { MemberList = _typeMap.MemberList, ReverseOf = _typeMap };
        _profile.TypeMaps.Add(typeMap);
        return new NonGenericMappingExpression(typeMap, null, _profile);
    }
}
