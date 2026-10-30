using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;

namespace AutoMapping.Internal;

/// <summary>
/// Turns a <see cref="TypeMap"/> (or an ad-hoc conversion such as List&lt;A&gt; to List&lt;B&gt;) into a single
/// expression tree: <c>(src, dest, ctx) =&gt; result</c>. Same-type members become plain assignments, so a
/// compiled map costs about as much as hand-written code. One builder instance is used per lambda being built.
/// </summary>
internal sealed class ExpressionBuilder
{
    private static readonly MethodInfo EnterMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.Enter))!;
    private static readonly MethodInfo ExitMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.Exit))!;
    private static readonly MethodInfo EnterTypeMapMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.EnterTypeMap))!;
    private static readonly MethodInfo ExitTypeMapMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.ExitTypeMap))!;
    private static readonly MethodInfo TryGetCachedMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.TryGetCached))!;
    private static readonly MethodInfo RememberMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.Remember))!;
    private static readonly MethodInfo OnBeforeMapMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.OnBeforeMap))!;
    private static readonly MethodInfo OnAfterMapMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.OnAfterMap))!;
    private static readonly MethodInfo MapRuntimeMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.MapRuntime))!;
    private static readonly MethodInfo CreateInstanceMethod = typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.CreateInstance))!;

    private readonly MapperConfiguration _cfg;
    private readonly ProfileSettings _settings;

    // Lean trees run with a null ResolutionContext: no depth tracking, no operation hooks, nested maps are called through MapLean.
    private readonly bool _lean;

    private int _inlineDepth;
    private const int MaxInlineDepth = 2;   // nested lean maps are emitted into the parent tree up to this depth

    public ExpressionBuilder(MapperConfiguration cfg, ProfileSettings settings, bool lean = false)
    {
        _cfg = cfg;
        _settings = settings;
        _lean = lean;
    }

    // ------------------------------------------------------------------ whole-type mapping

    public LambdaExpression BuildTypeMapLambda(TypeMap tm)
    {
        var sType = tm.SourceType;
        var dType = tm.DestinationType;
        var src = Expression.Parameter(sType, "src");
        var dest = Expression.Parameter(dType, "dest");
        var ctx = Expression.Parameter(typeof(ResolutionContext), "ctx");
        var ret = Expression.Label(dType, "ret");
        var body = new List<Expression>();
        var vars = new List<ParameterExpression>();

        if (!sType.IsValueType)
            body.Add(Expression.IfThen(Expression.ReferenceEqual(src, Expression.Constant(null, sType)), Expression.Return(ret, dest)));

        // ---- maps that replace the whole conversion
        if (tm.CustomConverter != null)
        {
            body.Add(Expression.Label(ret, InvokeDelegate(tm.CustomConverter, src, dest, ctx)));
            return Expression.Lambda(Expression.Block(body), src, dest, ctx);
        }
        if (tm.ConverterType != null)
        {
            var converterInterface = typeof(ITypeConverter<,>).MakeGenericType(sType, dType);
            if (!converterInterface.IsAssignableFrom(tm.ConverterType))
                throw new MappingConfigurationException($"{tm.ConverterType.Name} does not implement {TypeHelpers.Pretty(converterInterface)}.");

            var instance = Expression.Convert(Expression.Call(ctx, CreateInstanceMethod, Expression.Constant(tm.ConverterType, typeof(Type))), converterInterface);
            body.Add(Expression.Label(ret, Expression.Call(instance, converterInterface.GetMethod("Convert")!, src, dest, ctx)));
            return Expression.Lambda(Expression.Block(body), src, dest, ctx);
        }
        if (tm.AsType != null)
        {
            if (tm.AsType == dType)
                throw new MappingConfigurationException($"{tm.Pair}: As<{TypeHelpers.Pretty(tm.AsType)}>() must name a different (implementation) type.");

            var implementation = Convert(src, sType, tm.AsType, Expression.Default(tm.AsType), ctx);
            body.Add(Expression.Label(ret, Expression.Convert(implementation, dType)));
            return Expression.Lambda(Expression.Block(body), src, dest, ctx);
        }

        // ---- polymorphic dispatch to derived maps (most derived first)
        foreach (var derived in tm.DerivedTypeMaps.OrderByDescending(d => TypeHelpers.InheritanceDepth(d.SourceType)))
        {
            if (derived.SourceType == sType && derived.DestinationType == dType) continue;
            var holder = _cfg.GetMapping(derived.SourceType, derived.DestinationType);
            var map = holder.GetType().GetMethod("Map")!;
            Expression derivedDest = dType.IsValueType ? Expression.Default(derived.DestinationType) : Expression.TypeAs(dest, derived.DestinationType);
            var call = Expression.Call(Expression.Constant(holder, holder.GetType()), map, Expression.Convert(src, derived.SourceType), derivedDest, ctx);
            body.Add(Expression.IfThen(Expression.TypeIs(src, derived.SourceType), Expression.Return(ret, Expression.Convert(call, dType))));
        }

        if (tm.PreserveReferences && !sType.IsValueType)
        {
            var cached = Expression.Variable(typeof(object), "cached");
            vars.Add(cached);
            body.Add(Expression.IfThen(
                Expression.Call(ctx, TryGetCachedMethod, Expression.Convert(src, typeof(object)), Expression.Constant(dType), cached),
                Expression.Return(ret, Expression.Convert(cached, dType))));
        }

        if (tm.MaxDepth > 0)
        {
            body.Add(Expression.IfThen(
                Expression.Not(Expression.Call(ctx, EnterTypeMapMethod, Expression.Constant(tm.Id), Expression.Constant(tm.MaxDepth))),
                Expression.Return(ret, dest)));
        }

        if (!_lean) body.Add(Expression.Call(ctx, EnterMethod, Expression.Constant(tm.Id)));

        var result = Expression.Variable(dType, "result");
        vars.Add(result);
        var create = BuildConstruction(tm, src, ctx);
        body.Add(Expression.Assign(result, dType.IsValueType ? create : Expression.Coalesce(dest, create)));

        if (tm.PreserveReferences && !sType.IsValueType)
            body.Add(Expression.Call(ctx, RememberMethod, Expression.Convert(src, typeof(object)), Expression.Constant(dType), Expression.Convert(result, typeof(object))));

        if (!_lean)
        {
            var operationBefore = Expression.Field(ctx, nameof(ResolutionContext.OperationBefore));
            body.Add(Expression.IfThen(Expression.NotEqual(operationBefore, Expression.Constant(null, operationBefore.Type)),
                Expression.Call(ctx, OnBeforeMapMethod, Expression.Convert(src, typeof(object)), Expression.Convert(result, typeof(object)))));
        }
        foreach (var before in tm.BeforeMaps)
            body.Add(InvokeDelegate(before, src, result, ctx));

        foreach (var pm in tm.PropertyMaps)
        {
            if (pm.Ignored || !pm.IsResolved || pm.DestinationMember == null) continue;
            body.Add(BuildMemberAssignment(tm, pm, src, result, ctx));
        }

        foreach (var after in tm.AfterMaps)
            body.Add(InvokeDelegate(after, src, result, ctx));
        if (!_lean)
        {
            var operationAfter = Expression.Field(ctx, nameof(ResolutionContext.OperationAfter));
            body.Add(Expression.IfThen(Expression.NotEqual(operationAfter, Expression.Constant(null, operationAfter.Type)),
                Expression.Call(ctx, OnAfterMapMethod, Expression.Convert(src, typeof(object)), Expression.Convert(result, typeof(object)))));
        }

        if (tm.MaxDepth > 0) body.Add(Expression.Call(ctx, ExitTypeMapMethod, Expression.Constant(tm.Id)));
        if (!_lean) body.Add(Expression.Call(ctx, ExitMethod));
        body.Add(Expression.Label(ret, result));

        return Expression.Lambda(Expression.Block(dType, vars, body), src, dest, ctx);
    }

    private Expression BuildConstruction(TypeMap tm, ParameterExpression src, ParameterExpression ctx)
    {
        var d = tm.DestinationType;
        if (tm.ConstructUsing != null)
            return InvokeDelegate(tm.ConstructUsing, src, ctx);

        if (tm.ConstructUsingServiceLocator)
            return Expression.Convert(Expression.Call(ctx, CreateInstanceMethod, Expression.Constant(d, typeof(Type))), d);

        if (tm.Constructor != null)
        {
            var noDestination = Expression.Default(d);
            var args = tm.CtorParams.Select(p =>
                BuildConvertedValue(tm, p, src, noDestination, Expression.Default(p.DestinationType), Expression.Default(p.DestinationType), ctx));
            return Expression.New(tm.Constructor, args);
        }

        if (d.IsValueType) return Expression.Default(d);

        // Abstract/unconstructible destination: only fails if no destination instance was supplied and no derived map took over.
        return Expression.Throw(
            Expression.Constant(new MappingConfigurationException(
                tm.ConstructorError ?? $"{TypeHelpers.Pretty(d)} has no usable constructor.")),
            d);
    }

    // ------------------------------------------------------------------ members

    private Expression BuildMemberAssignment(TypeMap tm, PropertyMap pm, ParameterExpression src, ParameterExpression result, ParameterExpression ctx)
    {
        var type = pm.DestinationType;

        // ForPath: walk (and create) the parents first
        var parents = new List<Expression>();
        Expression target = result;
        if (pm.DestinationPath != null)
        {
            foreach (var parent in pm.DestinationPath)
            {
                var access = Expression.MakeMemberAccess(target, parent);
                if (access.Type.IsValueType)
                    throw new MappingConfigurationException($"{tm.Pair}: ForPath cannot go through the value-type member '{parent.Name}'.");
                if (access.Type.GetConstructor(Type.EmptyTypes) == null)
                    throw new MappingConfigurationException($"{tm.Pair}: ForPath needs {TypeHelpers.Pretty(access.Type)} to have a public parameterless constructor.");

                parents.Add(Expression.IfThen(TypeHelpers.IsNull(access), Expression.Assign(access, Expression.New(access.Type))));
                target = access;
            }
        }

        var leaf = Expression.MakeMemberAccess(target, pm.DestinationMember!);
        var hasPath = pm.DestinationPath != null;
        Expression current = hasPath ? Expression.Default(type) : leaf;

        Expression statement;

        // collections filled in place: get-only properties, or UseDestinationValue() on a settable one
        var populate = pm.IsGetOnlyCollection || (pm.UseDestinationValue == true && MemberMatcher.IsPopulatable(type));
        if (populate && !hasPath)
        {
            var raw = BuildRawValue(pm, src, result, current, ctx);
            var fill = BuildPopulate(raw, type, leaf, ctx);
            if (fill != null)
            {
                statement = Expression.IfThen(Expression.Not(TypeHelpers.IsNull(leaf)), fill);
                if (!pm.IsGetOnlyCollection)   // settable and currently null: assign a fresh collection instead
                {
                    var converted = ConvertRaw(tm, pm, raw, Expression.Default(type), ctx);
                    statement = Expression.IfThenElse(TypeHelpers.IsNull(leaf), Expression.Assign(leaf, converted), fill);
                }
                return WrapPreCondition(pm, statement, src, result, ctx);
            }
            if (pm.IsGetOnlyCollection)
                throw new MappingConfigurationException($"{tm.Pair}: cannot fill get-only member '{pm.DestinationName}' from {TypeHelpers.Pretty(raw.Type)}.");
        }

        // map onto an already-populated nested object instead of replacing it
        var equivalent = TypeHelpers.IsCollectionLike(type) && TypeHelpers.TryGetEnumerableElement(type, out var memberElement) &&
                         _cfg.HasEquivalencyFor(memberElement);
        var wantsExisting = !type.IsValueType && type != typeof(string) && (!TypeHelpers.IsCollectionLike(type) || equivalent) &&
                            pm.UseDestinationValue != false && !hasPath;
        Expression existing = wantsExisting ? leaf : Expression.Default(type);

        var value = BuildConvertedValue(tm, pm, src, result, current, existing, ctx);

        if (pm.Condition != null)
        {
            var temp = Expression.Variable(type, "value");
            var conditionCall = InvokeDelegate(pm.Condition, src, result, temp, current, ctx);
            var assign = Expression.Block(parents.Concat(new Expression[] { Expression.Assign(leaf, temp) }));
            statement = Expression.Block(new[] { temp }, Expression.Assign(temp, value), Expression.IfThen(conditionCall, assign));
        }
        else
        {
            statement = Expression.Block(parents.Concat(new Expression[] { Expression.Assign(leaf, value) }));
        }
        return WrapPreCondition(pm, statement, src, result, ctx);
    }

    private static Expression WrapPreCondition(PropertyMap pm, Expression statement, ParameterExpression src, ParameterExpression result, ParameterExpression ctx) =>
        pm.PreCondition == null
            ? statement
            : Expression.IfThen(InvokeDelegate(pm.PreCondition, src, result, ctx), statement);

    /// <summary>Resolves the source value (before conversion) for a member or constructor parameter.</summary>
    private Expression BuildRawValue(PropertyMap pm, ParameterExpression src, Expression destObject, Expression currentValue, ParameterExpression ctx)
    {
        if (pm.CustomSource != null)
        {
            var lambda = pm.CustomSource;
            return NullSafe(ParameterReplacer.Replace(lambda.Body, lambda.Parameters[0], src), src);
        }

        if (pm.Resolver != null)
        {
            switch (pm.ResolverKind)
            {
                case ResolverKind.Plain:
                    return InvokeDelegate(pm.Resolver, src, destObject, currentValue, ctx);
                case ResolverKind.SourceMember:
                    return InvokeDelegate(pm.Resolver, src, destObject, SourceMemberFor(pm, src), currentValue, ctx);
                case ResolverKind.Converter:
                    return InvokeDelegate(pm.Resolver, SourceMemberFor(pm, src), ctx);
            }
        }

        if (pm.HasConstant)
            return Expression.Constant(pm.Constant, pm.ConstantType ?? pm.DestinationType);

        if (pm.SourceChain != null)
            return NullSafe(ChainExpression(src, pm.SourceChain), src);

        throw new MappingConfigurationException($"No source for destination member '{pm.DestinationName}'.");
    }

    private Expression SourceMemberFor(PropertyMap pm, ParameterExpression src)
    {
        if (pm.ResolverSource != null)
            return NullSafe(ParameterReplacer.Replace(pm.ResolverSource.Body, pm.ResolverSource.Parameters[0], src), src);

        var chain = _settings.Matcher.Resolve(pm.DestinationName, src.Type)
                    ?? throw new MappingConfigurationException(
                        $"No source member matches '{pm.DestinationName}' for its value converter/resolver. Name the source member explicitly.");
        return NullSafe(ChainExpression(src, chain), src);
    }

    private static Expression ChainExpression(Expression root, IEnumerable<MemberInfo> chain)
    {
        Expression current = root;
        foreach (var member in chain)
            current = member is MethodInfo method ? Expression.Call(current, method) : Expression.MakeMemberAccess(current, member);
        return current;
    }

    /// <summary>Resolves the raw source value for a member/ctor-param and converts it to the destination type.</summary>
    private Expression BuildConvertedValue(TypeMap tm, PropertyMap pm, ParameterExpression src, Expression destObject,
        Expression currentValue, Expression existing, ParameterExpression ctx)
    {
        var raw = BuildRawValue(pm, src, destObject, currentValue, ctx);
        return ConvertRaw(tm, pm, raw, existing, ctx);
    }

    private Expression ConvertRaw(TypeMap tm, PropertyMap pm, Expression raw, Expression existing, ParameterExpression ctx)
    {
        var d = pm.DestinationType;

        Expression converted = Once(raw, r =>
        {
            Expression inner = pm.MapAtRuntime
                ? RuntimeConvert(r, d, ctx)
                : Convert(r, r.Type, d, existing, ctx);

            // AllowNull(): a null source gives a null destination, even for collections
            if (pm.AllowNull == true && TypeHelpers.CanBeNull(r.Type) && TypeHelpers.CanBeNull(d))
                inner = Expression.Condition(TypeHelpers.IsNull(r), Expression.Default(d), inner, d);
            return inner;
        });

        // DoNotAllowNull() / AllowNullDestinationValues = false: null becomes an empty value
        var forbidNull = pm.AllowNull == false || (pm.AllowNull == null && !_settings.AllowNullDestinationValues);
        if (forbidNull && !d.IsValueType && EmptyValue(d) is { } empty)
            converted = Once(converted, v => Expression.Condition(TypeHelpers.IsNull(v), empty, v, d));

        if (pm.HasNullSubstitute && TypeHelpers.CanBeNull(d) && pm.NullSubstitute != null)
        {
            var substitute = ConstantAs(pm.NullSubstitute, d);
            converted = Once(converted, v => Expression.Condition(TypeHelpers.IsNull(v), substitute, v, d));
        }

        foreach (var (type, transformer) in _settings.ValueTransformers.Concat(tm.ValueTransformers))   // profile-wide first, then per map
        {
            if (type != d) continue;
            converted = Once(converted, v =>
            {
                var transformed = ParameterReplacer.Replace(transformer.Body, transformer.Parameters[0], v);
                return TypeHelpers.CanBeNull(d)
                    ? Expression.Condition(TypeHelpers.IsNull(v), v, transformed, d)   // transformers never see null
                    : transformed;
            });
        }
        return converted;
    }

    private static Expression ConstantAs(object value, Type d)
    {
        var valueType = value.GetType();
        if (valueType == d) return Expression.Constant(value, d);
        return Expression.Convert(Expression.Constant(value, valueType), d);
    }

    private static Expression? EmptyValue(Type d)
    {
        if (d == typeof(string)) return Expression.Constant(string.Empty, typeof(string));
        if (d.IsArray) return Expression.Call(typeof(Array), nameof(Array.Empty), new[] { d.GetElementType()! });
        if (!d.IsAbstract && !d.IsInterface && d.GetConstructor(Type.EmptyTypes) != null) return Expression.New(d);
        if (d.IsGenericType && TypeHelpers.TryGetEnumerableElement(d, out var element))
        {
            var list = typeof(List<>).MakeGenericType(element);
            if (d.IsAssignableFrom(list)) return Expression.Convert(Expression.New(list), d);
        }
        return null;
    }

    private static Expression RuntimeConvert(Expression value, Type d, ParameterExpression ctx) =>
        Expression.Convert(
            Expression.Call(ctx, MapRuntimeMethod, Expression.Convert(value, typeof(object)), Expression.Constant(d, typeof(Type))),
            d);

    /// <summary>Invokes a delegate constant, converting each argument to the delegate's parameter type.</summary>
    private static Expression InvokeDelegate(Delegate target, params Expression[] args)
    {
        var parameters = target.GetType().GetMethod("Invoke")!.GetParameters();
        if (parameters.Length != args.Length)
            throw new MappingConfigurationException($"Internal error: delegate {target.GetType().Name} expects {parameters.Length} arguments, got {args.Length}.");

        var converted = new Expression[args.Length];
        for (var i = 0; i < args.Length; i++)
            converted[i] = args[i].Type == parameters[i].ParameterType ? args[i] : Expression.Convert(args[i], parameters[i].ParameterType);
        return Expression.Invoke(Expression.Constant(target), converted);
    }

    // ------------------------------------------------------------------ in-place collections

    private Expression? BuildPopulate(Expression raw, Type d, Expression current, ParameterExpression ctx)
    {
        var s = raw.Type;

        var destDictionary = TypeHelpers.FindGenericInterface(d, typeof(IDictionary<,>));
        if (destDictionary != null && TypeHelpers.TryGetKeyValueElement(s, out var ks, out var vs))
        {
            var arguments = destDictionary.GetGenericArguments();
            var kd = arguments[0];
            var vd = arguments[1];
            EnsureBuildable(ks, kd);
            EnsureBuildable(vs, vd);

            var keyMap = ElementMapping(ks, kd);
            var valueMap = ElementMapping(vs, vd);
            var source = Expression.Convert(raw, typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(ks, vs)));
            return Expression.Call(
                typeof(CollectionMappers).GetMethod(nameof(CollectionMappers.PopulateDictionary))!.MakeGenericMethod(ks, vs, kd, vd),
                source, Expression.Convert(current, destDictionary), keyMap, valueMap, ctx);
        }

        var destCollection = TypeHelpers.FindGenericInterface(d, typeof(ICollection<>));
        if (destCollection != null && TypeHelpers.TryGetEnumerableElement(s, out var se))
        {
            var de = destCollection.GetGenericArguments()[0];
            EnsureBuildable(se, de);
            return Expression.Call(
                typeof(CollectionMappers).GetMethod(nameof(CollectionMappers.Populate))!.MakeGenericMethod(se, de),
                Expression.Convert(raw, typeof(IEnumerable<>).MakeGenericType(se)),
                Expression.Convert(current, destCollection),
                ElementMapping(se, de),
                ctx);
        }
        return null;
    }

    private ConstantExpression ElementMapping(Type s, Type d)
    {
        var holderType = typeof(Mapping<,>).MakeGenericType(s, d);
        return s == d && TypeHelpers.IsImmutableLeaf(s)
            ? Expression.Constant(null, holderType)
            : Expression.Constant(_cfg.GetMapping(s, d), holderType);
    }

    // ------------------------------------------------------------------ ad-hoc conversions (collections, scalars, top-level)

    public LambdaExpression BuildConversionLambda(Type s, Type d)
    {
        var src = Expression.Parameter(s, "src");
        var dest = Expression.Parameter(d, "dest");
        var ctx = Expression.Parameter(typeof(ResolutionContext), "ctx");
        var body = Convert(src, s, d, dest, ctx);
        return Expression.Lambda(body, src, dest, ctx);
    }

    /// <summary>A class/struct that is neither a scalar nor a collection (candidate for property-bag conversion).</summary>
    internal static bool IsPlainObject(Type t) =>
        !TypeHelpers.IsLeaf(t) && t != typeof(object) && !t.IsInterface && !t.IsAbstract && !t.IsArray && !t.IsPointer &&
        !TypeHelpers.TryGetEnumerableElement(t, out _);

    /// <summary>Returns an expression of exactly type <paramref name="d"/>.</summary>
    public Expression Convert(Expression value, Type s, Type d, Expression existing, ParameterExpression ctx)
    {
        // 1. explicit map (exact, or inherited through a base source type)
        var tm = _cfg.FindTypeMap(s, d);
        if (tm != null)
        {
            var arg = value.Type == tm.SourceType ? value : Expression.Convert(value, tm.SourceType);
            var dst = existing.Type == tm.DestinationType ? existing : Expression.Default(tm.DestinationType);

            // a lean nested map cannot reach itself, so it can be inlined: no holder, delegate call or closure hop per member
            if (_lean && _inlineDepth < MaxInlineDepth && _cfg.IsLeanMapping(tm, tm.SourceType, tm.DestinationType))
            {
                var inlined = new ExpressionBuilder(_cfg, tm.Settings, true) { _inlineDepth = _inlineDepth + 1 }.BuildTypeMapLambda(tm);
                var inv = Expression.Invoke(inlined, arg, dst, Expression.Constant(null, typeof(ResolutionContext)));
                return inv.Type == d ? inv : Expression.Convert(inv, d);
            }

            var holder = _cfg.GetMapping(tm.SourceType, tm.DestinationType);
            var call = _lean
                ? Expression.Call(Expression.Constant(holder, holder.GetType()), holder.GetType().GetMethod("MapLean")!, arg, dst)
                : Expression.Call(Expression.Constant(holder, holder.GetType()), holder.GetType().GetMethod("Map")!, arg, dst, ctx);
            return call.Type == d ? call : Expression.Convert(call, d);
        }

        // fast path: identical immutable scalars (int, string, DateTime, int?, enums, ...) are plain assignments
        if (s == d && TypeHelpers.IsLeaf(s)) return value;

        // 2. nullable value types
        var sNull = Nullable.GetUnderlyingType(s);
        var dNull = Nullable.GetUnderlyingType(d);
        if (sNull != null)
        {
            return Once(value, v =>
            {
                var inner = Convert(Expression.Property(v, "Value"), sNull, dNull ?? d, Expression.Default(dNull ?? d), ctx);
                Expression wrapped = dNull != null ? Expression.Convert(inner, d) : inner;
                return Expression.Condition(Expression.Property(v, "HasValue"), wrapped, Expression.Default(d), d);
            });
        }
        if (dNull != null)
        {
            if (s == typeof(string))
                return Expression.Call(typeof(Converters).GetMethod(nameof(Converters.FromStringNullable))!.MakeGenericMethod(dNull), value);
            return Expression.Convert(Convert(value, s, dNull, Expression.Default(dNull), ctx), d);
        }

        // 2b. dictionary <-> object ("property bag")
        if (TypeHelpers.TryGetKeyValueElement(s, out var bagKey, out var bagValue) && bagKey == typeof(string) &&
            !TypeHelpers.IsBuildableDictionary(d, out _, out _) && IsPlainObject(d))
        {
            var plan = Activator.CreateInstance(typeof(DictionaryToObjectPlan<>).MakeGenericType(d),
                BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { _cfg, _settings.Matcher }, null)!;
            var source = Expression.Convert(value, typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(typeof(string), bagValue)));
            var existingTyped = existing.Type == d ? existing : Expression.Default(d);
            return Expression.Call(Expression.Constant(plan), plan.GetType().GetMethod("Map")!.MakeGenericMethod(bagValue), source, existingTyped, ctx);
        }
        if (TypeHelpers.IsBuildableDictionary(d, out var toKey, out var toValue) && toKey == typeof(string) &&
            !TypeHelpers.TryGetEnumerableElement(s, out _) && IsPlainObject(s))
        {
            var plan = Activator.CreateInstance(typeof(ObjectToDictionaryPlan<>).MakeGenericType(s),
                BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { _cfg, _settings.Matcher }, null)!;
            var call = Expression.Call(Expression.Constant(plan), plan.GetType().GetMethod("Map")!.MakeGenericMethod(toValue),
                value, Expression.Constant(_settings.AllowNullCollections), ctx);
            return call.Type == d ? call : Expression.Convert(call, d);
        }

        // 3. dictionaries
        if (TypeHelpers.IsBuildableDictionary(d, out var kd, out var vd) && TypeHelpers.TryGetKeyValueElement(s, out var ks, out var vs))
            return BuildDictionary(value, ks, vs, d, kd, vd, ctx);

        // 4. collections (always copied into a new destination collection)
        if (TypeHelpers.TryGetEnumerableElement(d, out var de) && TypeHelpers.TryGetEnumerableElement(s, out var se))
        {
            // collection equivalency (EqualityComparison): update matching items of the existing collection in place
            if (existing.Type == d && d.IsAssignableFrom(typeof(List<>).MakeGenericType(de)) && _cfg.FindTypeMap(se, de) is { EqualityComparison: not null } equality)
                return BuildEquivalent(value, existing, se, de, d, equality, ctx);
            // AutoMapper maps into a destination collection that was passed in (clear + add), so callers that ignore the return value still see the items
            if (existing.Type == d && !d.IsArray && TypeHelpers.FindGenericInterface(d, typeof(ICollection<>)) != null &&
                BuildPopulate(value, d, existing, ctx) is { } fill)
            {
                var fresh = BuildCollection(value, s, se, d, de, ctx);
                return Expression.Condition(
                    Expression.NotEqual(existing, Expression.Constant(null, d)),
                    Expression.Block(d, fill, existing),
                    fresh, d);
            }
            return BuildCollection(value, s, se, d, de, ctx);
        }

        // 5. assignable (same type, base type, interface, boxing) - shallow, like AutoMapper without a map
        if (d.IsAssignableFrom(s))
            return value.Type == d ? value : Expression.Convert(value, d);

        // 5b. user-defined implicit/explicit operators between reference types (e.g. string -> JsonObject<T>), like AutoMapper
        if (!s.IsValueType && !d.IsValueType && d != typeof(string) && HasConversionOperator(s, d))
            return Once(value, v => Expression.Condition(
                Expression.Equal(v, Expression.Constant(null, s)), Expression.Default(d), Expression.Convert(v, d), d));

        // 6. strings
        if (d == typeof(string)) return ToStringExpression(value, s);
        if (s == typeof(string)) return FromStringExpression(value, d);

        // 7. enums
        if (s.IsEnum || d.IsEnum) return ConvertEnum(value, s, d, ctx);

        // 8. numerics and other IConvertible primitives
        if (TypeHelpers.IsNumericLike(s) && TypeHelpers.IsNumericLike(d)) return Expression.Convert(value, d);
        if (TypeHelpers.IsConvertiblePrimitive(s) && TypeHelpers.IsConvertiblePrimitive(d))
            return Expression.Call(typeof(Converters).GetMethod(nameof(Converters.ChangeType))!.MakeGenericMethod(d), Expression.Convert(value, typeof(object)));

        // 9. user-defined conversion operators on value types
        if (s.IsValueType || d.IsValueType)
        {
            try { return Expression.Convert(value, d); }
            catch (InvalidOperationException) { /* fall through */ }
        }

        // 10. complex type without a map
        if (_settings.CreateMissingTypeMaps && !TypeHelpers.IsLeaf(s) && !TypeHelpers.IsLeaf(d))
        {
            _cfg.GetOrCreateTypeMap(s, d);
            return Convert(value, s, d, existing, ctx);
        }

        throw new MappingConfigurationException(
            $"Missing map from {TypeHelpers.Pretty(s)} to {TypeHelpers.Pretty(d)}. " +
            $"Call CreateMap<{TypeHelpers.Pretty(s)}, {TypeHelpers.Pretty(d)}>() or set CreateMissingTypeMaps = true.");
    }

    /// <summary>
    /// Element conversions are compiled lazily by their own <see cref="Mapping{TSource,TDestination}"/>, so a missing map would
    /// otherwise only surface on the first element at runtime. Building (not compiling) the conversion here reports it
    /// when the parent map is built, which is what <see cref="MapperConfiguration.AssertConfigurationIsValid()"/> relies on.
    /// </summary>
    private void EnsureBuildable(Type s, Type d)
    {
        if (s == d && TypeHelpers.IsImmutableLeaf(s)) return;
        if (_cfg.FindTypeMap(s, d) != null) return;

        // a type can be a collection of itself (e.g. JToken : IEnumerable<JToken>): a pair already being built is a cycle
        var building = _buildingPairs ??= new HashSet<(Type, Type)>();
        if (!building.Add((s, d))) return;
        try { BuildConversionLambda(s, d); }
        finally { building.Remove((s, d)); }
    }

    [ThreadStatic] private static HashSet<(Type, Type)>? _buildingPairs;

    private Expression BuildEquivalent(Expression value, Expression existing, Type se, Type de, Type d, TypeMap equality, ParameterExpression ctx)
    {
        EnsureBuildable(se, de);
        // the comparison may belong to a base-source map: adapt it to (TSource, TDestination)
        var comparison = equality.EqualityComparison!;
        var invoke = comparison.GetType().GetMethod("Invoke")!.GetParameters();
        var a = Expression.Parameter(se, "a");
        var b = Expression.Parameter(de, "b");
        var adapted = Expression.Lambda(
            typeof(Func<,,>).MakeGenericType(se, de, typeof(bool)),
            Expression.Invoke(Expression.Constant(comparison),
                a.Type == invoke[0].ParameterType ? a : Expression.Convert(a, invoke[0].ParameterType),
                b.Type == invoke[1].ParameterType ? b : Expression.Convert(b, invoke[1].ParameterType)),
            a, b).Compile();

        var method = typeof(CollectionMappers).GetMethod(nameof(CollectionMappers.Equivalent))!.MakeGenericMethod(se, de);
        var call = Expression.Call(method,
            Expression.Convert(value, typeof(IEnumerable<>).MakeGenericType(se)),
            Expression.TypeAs(existing, typeof(ICollection<>).MakeGenericType(de)),
            Expression.Constant(adapted),
            ElementMapping(se, de),
            ctx,
            Expression.Constant(_settings.AllowNullCollections));
        return Expression.Convert(call, d);
    }

    private Expression BuildCollection(Expression value, Type s, Type se, Type d, Type de, ParameterExpression ctx)
    {
        EnsureBuildable(se, de);
        var elem = ElementMapping(se, de);
        var allowNull = Expression.Constant(_settings.AllowNullCollections);
        var source = Expression.Convert(value, typeof(IEnumerable<>).MakeGenericType(se));

        MethodInfo method;

        if (d.IsArray)
        {
            method = Helper(nameof(CollectionMappers.ToArray)).MakeGenericMethod(se, de);
        }
        else if (d.IsGenericType)
        {
            var def = d.GetGenericTypeDefinition();
            if (def == typeof(List<>) || def == typeof(IList<>) || def == typeof(ICollection<>) || def == typeof(IEnumerable<>) ||
                def == typeof(IReadOnlyList<>) || def == typeof(IReadOnlyCollection<>))
            {
                method = Helper(nameof(CollectionMappers.ToList)).MakeGenericMethod(se, de);
            }
            else if (def == typeof(HashSet<>) || def == typeof(ISet<>) || def.FullName == "System.Collections.Generic.IReadOnlySet`1")
            {
                method = Helper(nameof(CollectionMappers.ToHashSet)).MakeGenericMethod(se, de);
            }
            else if (def == typeof(ReadOnlyCollection<>))
            {
                method = Helper(nameof(CollectionMappers.ToReadOnlyCollection)).MakeGenericMethod(se, de);
            }
            else if (!d.IsAbstract && !d.IsInterface && d.GetConstructor(Type.EmptyTypes) != null && typeof(ICollection<>).MakeGenericType(de).IsAssignableFrom(d))
            {
                method = Helper(nameof(CollectionMappers.ToCollection)).MakeGenericMethod(se, de, d);
            }
            else
            {
                throw Unsupported(s, d);
            }
        }
        else if (!d.IsAbstract && !d.IsInterface && d.GetConstructor(Type.EmptyTypes) != null && typeof(ICollection<>).MakeGenericType(de).IsAssignableFrom(d))
        {
            method = Helper(nameof(CollectionMappers.ToCollection)).MakeGenericMethod(se, de, d);
        }
        else
        {
            throw Unsupported(s, d);
        }

        var call = Expression.Call(method, source, elem, ctx, allowNull);
        return call.Type == d ? call : Expression.Convert(call, d);

        static MethodInfo Helper(string name) => typeof(CollectionMappers).GetMethod(name)!;
        static MappingConfigurationException Unsupported(Type from, Type to) =>
            new($"Cannot map collection {TypeHelpers.Pretty(from)} to {TypeHelpers.Pretty(to)}: unsupported destination collection type.");
    }

    private Expression BuildDictionary(Expression value, Type ks, Type vs, Type d, Type kd, Type vd, ParameterExpression ctx)
    {
        EnsureBuildable(ks, kd);
        EnsureBuildable(vs, vd);

        var source = Expression.Convert(value, typeof(IEnumerable<>).MakeGenericType(typeof(KeyValuePair<,>).MakeGenericType(ks, vs)));
        var call = Expression.Call(
            typeof(CollectionMappers).GetMethod(nameof(CollectionMappers.ToDictionary))!.MakeGenericMethod(ks, vs, kd, vd),
            source, ElementMapping(ks, kd), ElementMapping(vs, vd), ctx, Expression.Constant(_settings.AllowNullCollections));
        return call.Type == d ? call : Expression.Convert(call, d);
    }

    // ------------------------------------------------------------------ scalars

    private static Expression ToStringExpression(Expression value, Type s)
    {
        if (s.IsEnum)
            return Expression.Call(typeof(Converters).GetMethod(nameof(Converters.EnumToString))!.MakeGenericMethod(s), value);

        var formatted = s.GetMethod("ToString", new[] { typeof(IFormatProvider) });
        if (s.IsValueType && formatted != null)
            return Expression.Call(value, formatted, Expression.Constant(CultureInfo.InvariantCulture, typeof(IFormatProvider)));

        return Expression.Call(typeof(Converters).GetMethod(nameof(Converters.AnyToString))!.MakeGenericMethod(s), value);
    }

    private static bool HasConversionOperator(Type s, Type d) =>
        s.GetMethods(BindingFlags.Public | BindingFlags.Static).Concat(d.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Any(m => (m.Name == "op_Implicit" || m.Name == "op_Explicit") && m.ReturnType == d &&
                      m.GetParameters() is { Length: 1 } p && p[0].ParameterType == s);

    private static Expression FromStringExpression(Expression value, Type d)
    {
        if (d.IsEnum)
            return Expression.Call(typeof(Converters).GetMethod(nameof(Converters.ParseEnum))!.MakeGenericMethod(d), value);
        var call = Expression.Call(typeof(Converters).GetMethod(nameof(Converters.FromString))!.MakeGenericMethod(d), value);
        return call.Type == d ? call : Expression.Convert(call, d);
    }

    private Expression ConvertEnum(Expression value, Type s, Type d, ParameterExpression ctx)
    {
        if (s.IsEnum && d.IsEnum)
            return Expression.Call(typeof(Converters).GetMethod(nameof(Converters.EnumToEnum))!.MakeGenericMethod(s, d), value);

        if (s.IsEnum)
        {
            var underlying = Enum.GetUnderlyingType(s);
            return Convert(Expression.Convert(value, underlying), underlying, d, Expression.Default(d), ctx);
        }

        var target = Enum.GetUnderlyingType(d);
        return Expression.Convert(Convert(value, s, target, Expression.Default(target), ctx), d);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Evaluates <paramref name="value"/> once, then builds <paramref name="body"/> over a cheap reference to it.</summary>
    private static Expression Once(Expression value, Func<Expression, Expression> body)
    {
        if (value is ParameterExpression or ConstantExpression) return body(value);
        var temp = Expression.Variable(value.Type, "tmp");
        var inner = body(temp);
        return Expression.Block(inner.Type, new[] { temp }, Expression.Assign(temp, value), inner);
    }

    /// <summary>
    /// Rewrites member/method chains so a null link yields default(T) instead of throwing, mirroring
    /// AutoMapper's null-propagation. The root source parameter is known non-null and is never guarded.
    /// </summary>
    private static Expression NullSafe(Expression e, Expression root)
    {
        switch (e)
        {
            case MemberExpression { Expression: not null } m:
            {
                var inner = NullSafe(m.Expression, root);
                if (TypeHelpers.IsNullableValueType(inner.Type) && m.Member.Name == "Value")
                    return Expression.Call(inner, inner.Type.GetMethod("GetValueOrDefault", Type.EmptyTypes)!);
                return Guard(inner, root, t => Expression.MakeMemberAccess(t, m.Member), m.Type);
            }
            case MethodCallExpression { Object: not null } c:
            {
                var inner = NullSafe(c.Object, root);
                return Guard(inner, root, t => Expression.Call(t, c.Method, c.Arguments), c.Type);
            }
            case UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } u:
                return Expression.MakeUnary(u.NodeType, NullSafe(u.Operand, root), u.Type, u.Method);
            default:
                return e;
        }
    }

    private static Expression Guard(Expression inner, Expression root, Func<Expression, Expression> apply, Type resultType)
    {
        if (inner == root || inner.Type.IsValueType) return apply(inner);
        return Once(inner, t => Expression.Condition(
            Expression.ReferenceEqual(t, Expression.Constant(null, t.Type)),
            Expression.Default(resultType),
            apply(t),
            resultType));
    }
}
