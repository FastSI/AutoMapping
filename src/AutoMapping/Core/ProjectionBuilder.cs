using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AutoMapping.Internal;

/// <summary>
/// Builds <c>src =&gt; new Dto { ... }</c> expression trees with no delegate calls or blocks, so LINQ providers such as
/// EF Core can translate them to SQL. Supports member copy, flattening, MapFrom expressions, nested maps, collections,
/// constructors and numeric/enum conversions. Delegates (ConvertUsing, resolvers, converters, hooks) cannot be translated
/// and throw; conditions, MapAtRuntime and get-only collection members are not part of a projection.
/// </summary>
internal sealed class ProjectionBuilder
{
    private const int MaxNesting = 32;   // hard stop; recursive maps are governed by MaxDepth
    private readonly Dictionary<int, int> _active = new();   // type map id -> times it is on the current path (one builder per Build)
    private readonly MapperConfiguration _cfg;

    public ProjectionBuilder(MapperConfiguration cfg) => _cfg = cfg;

    public LambdaExpression Build(Type source, Type destination, ISet<string>? membersToExpand)
    {
        var parameter = Expression.Parameter(source, "src");
        var body = BuildInit(parameter, source, destination, 0, string.Empty, membersToExpand)
                   ?? throw new MappingConfigurationException($"Cannot project {TypeHelpers.Pretty(source)} to {TypeHelpers.Pretty(destination)}.");
        return Expression.Lambda(body, parameter);
    }

    /// <summary>Dotted member path of <c>d =&gt; d.Orders</c> / <c>d =&gt; d.Customer.Address</c>.</summary>
    public static string PathOf<TDestination>(Expression<Func<TDestination, object>> member) =>
        string.Join('.', (ConfigHelpers.MemberChain(member.Body)
                          ?? throw new ArgumentException("membersToExpand needs plain member paths such as d => d.Orders.", nameof(member)))
            .Select(m => m.Name));

    private Expression? BuildInit(Expression src, Type s, Type d, int depth, string path, ISet<string>? expand)
    {
        if (depth > MaxNesting)
            throw new MappingConfigurationException($"ProjectTo nesting for {TypeHelpers.Pretty(s)} -> {TypeHelpers.Pretty(d)} exceeds {MaxNesting} levels.");

        var tm = _cfg.FindTypeMap(s, d)
                 ?? throw new MappingConfigurationException($"ProjectTo needs a map from {TypeHelpers.Pretty(s)} to {TypeHelpers.Pretty(d)}.");

        _active.TryGetValue(tm.Id, out var times);
        if (times > 0)
        {
            // the map is reached again through itself: a query cannot be infinitely deep
            if (tm.MaxDepth == 0)
                throw new MappingConfigurationException(
                    $"ProjectTo cannot translate the recursive map {tm.Pair}. Set MaxDepth(n) on it, or Ignore the self-referencing member.");
            if (times >= tm.MaxDepth) return null;   // member stays at its default
        }
        _active[tm.Id] = times + 1;
        try { return BuildInitCore(src, s, d, tm, depth, path, expand); }
        finally { _active[tm.Id] = times; }
    }

    private Expression? BuildInitCore(Expression src, Type s, Type d, TypeMap tm, int depth, string path, ISet<string>? expand)
    {
        if (tm.ConvertExpression != null)
        {
            var body = ParameterReplacer.Replace(tm.ConvertExpression.Body, tm.ConvertExpression.Parameters[0], src);
            return body.Type == d ? body : Expression.Convert(body, d);
        }
        if (tm.CustomConverter != null || tm.ConverterType != null)
            throw new NotSupportedException($"{tm.Pair}: ConvertUsing with a delegate or converter class cannot be translated by ProjectTo. Use ConvertUsing(expression).");
        if (tm.ConstructUsing != null || tm.ConstructUsingServiceLocator)
            throw new NotSupportedException($"{tm.Pair}: ConstructUsing cannot be translated by ProjectTo.");
        if (tm.AsType != null)
        {
            var implementation = BuildInit(src, tm.SourceType, tm.AsType, depth, path, expand);
            return implementation == null ? null : Expression.Convert(implementation, d);
        }

        NewExpression creation;
        if (tm.Constructor != null)
        {
            var args = new List<Expression>();
            foreach (var p in tm.CtorParams)
                args.Add(ProjectValue(tm, p, src, depth, path, expand) ?? Expression.Default(p.DestinationType));
            creation = Expression.New(tm.Constructor, args);
        }
        else if (d.IsValueType || d.GetConstructor(Type.EmptyTypes) != null)
        {
            creation = Expression.New(d);
        }
        else
        {
            throw new MappingConfigurationException(tm.ConstructorError ?? $"{TypeHelpers.Pretty(d)} cannot be constructed for projection.");
        }

        var bindings = new List<MemberBinding>();
        foreach (var pm in tm.PropertyMaps)
        {
            if (pm.Ignored || !pm.IsResolved || pm.DestinationMember == null || pm.IsGetOnlyCollection || !IsPubliclyWritable(pm.DestinationMember)) continue;
            if (pm.DestinationPath != null)
                throw new NotSupportedException($"{tm.Pair}: ForPath member '{pm.DestinationName}' cannot be translated by ProjectTo.");
            if (pm.ExplicitExpansion && (expand == null || !expand.Contains(path + pm.DestinationName))) continue;

            var value = ProjectValue(tm, pm, src, depth, path, expand);
            if (value != null) bindings.Add(Expression.Bind(pm.DestinationMember, value));
        }
        return Expression.MemberInit(creation, bindings);
    }

    private Expression? ProjectValue(TypeMap tm, PropertyMap pm, Expression src, int depth, string path, ISet<string>? expand)
    {
        var d = pm.DestinationType;
        if (pm.Resolver != null)
            throw new NotSupportedException(
                $"{tm.Pair}: member '{pm.DestinationName}' uses a resolver/converter delegate, which ProjectTo cannot translate. Use an expression MapFrom.");

        Expression raw;
        if (pm.CustomSource != null)
            raw = ParameterReplacer.Replace(pm.CustomSource.Body, pm.CustomSource.Parameters[0], src);
        else if (pm.HasConstant)
            raw = Expression.Constant(pm.Constant, pm.ConstantType ?? d);
        else if (pm.SourceChain != null)
            raw = BuildChain(src, pm.SourceChain, _cfg.Options.EnableNullPropagationForQueryMapping);
        else
            return null;

        var converted = ProjectConvert(raw, raw.Type, d, depth, path + pm.DestinationName + ".", expand);
        if (converted != null && pm.WhenExpression != null)
        {
            var test = ParameterReplacer.Replace(pm.WhenExpression.Body, pm.WhenExpression.Parameters[0], src);
            converted = Expression.Condition(test, converted, Expression.Default(converted.Type));
        }
        if (converted != null && pm.HasNullSubstitute && pm.NullSubstitute != null && TypeHelpers.CanBeNull(d))
        {
            var substitute = pm.NullSubstitute.GetType() == d
                ? Expression.Constant(pm.NullSubstitute, d)
                : (Expression)Expression.Convert(Expression.Constant(pm.NullSubstitute), d);
            converted = Expression.Coalesce(converted, substitute);   // EF translates this to COALESCE
        }
        return converted;
    }

    private static bool IsPubliclyWritable(MemberInfo member) =>
        member is FieldInfo { IsPublic: true } || member is PropertyInfo { SetMethod.IsPublic: true };

    private static Expression BuildChain(Expression src, IReadOnlyList<MemberInfo> chain, bool nullGuards)
    {
        Expression current = src;
        var guards = new List<Expression>();
        foreach (var member in chain)
        {
            if (!current.Type.IsValueType && current != src) guards.Add(current);
            current = member is MethodInfo method ? Expression.Call(current, method) : Expression.MakeMemberAccess(current, member);
        }
        if (guards.Count == 0 || !nullGuards) return current;

        var test = guards
            .Select(g => (Expression)Expression.NotEqual(g, Expression.Constant(null, g.Type)))
            .Aggregate(Expression.AndAlso);
        return Expression.Condition(test, current, Expression.Default(current.Type));
    }

    private Expression? ProjectConvert(Expression value, Type s, Type d, int depth, string path, ISet<string>? expand)
    {
        var tm = _cfg.FindTypeMap(s, d);
        if (tm != null)
        {
            var init = BuildInit(value, tm.SourceType, tm.DestinationType, depth + 1, path, expand);
            if (init == null) return null;
            return s.IsValueType
                ? init
                : Expression.Condition(Expression.Equal(value, Expression.Constant(null, s)), Expression.Default(d), init, d);
        }

        if (s == d && TypeHelpers.IsLeaf(s)) return value;

        var sNull = Nullable.GetUnderlyingType(s);
        var dNull = Nullable.GetUnderlyingType(d);
        if (sNull != null || dNull != null)
            return Expression.Convert(value, d);

        if (TypeHelpers.IsCollectionLike(s) && TypeHelpers.IsCollectionLike(d))
        {
            TypeHelpers.TryGetEnumerableElement(s, out var se);
            TypeHelpers.TryGetEnumerableElement(d, out var de);

            Expression sequence = value;
            if (_cfg.FindTypeMap(se, de) is { } elementMap)
            {
                var item = Expression.Parameter(se, "e");
                var init = BuildInit(item, elementMap.SourceType, elementMap.DestinationType, depth + 1, path, expand);
                if (init == null) return null;
                sequence = Expression.Call(typeof(Enumerable), nameof(Enumerable.Select), new[] { se, de }, value, Expression.Lambda(init, item));
            }
            else if (se != de && TypeHelpers.IsLeaf(se) && TypeHelpers.IsLeaf(de))
            {
                var item = Expression.Parameter(se, "e");
                sequence = Expression.Call(typeof(Enumerable), nameof(Enumerable.Select), new[] { se, de }, value, Expression.Lambda(Expression.Convert(item, de), item));
            }
            else if (se != de && !de.IsAssignableFrom(se))
            {
                throw new NotSupportedException($"ProjectTo: no map from element type {TypeHelpers.Pretty(se)} to {TypeHelpers.Pretty(de)}.");
            }

            if (d.IsArray) return Expression.Call(typeof(Enumerable), nameof(Enumerable.ToArray), new[] { de }, sequence);
            if (d.IsAssignableFrom(typeof(List<>).MakeGenericType(de)))
                return Expression.Convert(Expression.Call(typeof(Enumerable), nameof(Enumerable.ToList), new[] { de }, sequence), d);
            if (d.IsGenericType && d.GetGenericTypeDefinition() == typeof(HashSet<>))
                return Expression.New(d.GetConstructor(new[] { typeof(IEnumerable<>).MakeGenericType(de) })!, sequence);
            throw new NotSupportedException($"ProjectTo: unsupported destination collection {TypeHelpers.Pretty(d)}.");
        }

        if (d.IsAssignableFrom(s)) return value.Type == d ? value : Expression.Convert(value, d);
        if (d == typeof(string)) return Expression.Call(value, s.GetMethod("ToString", Type.EmptyTypes)!);
        if (TypeHelpers.IsNumericLike(s) && TypeHelpers.IsNumericLike(d)) return Expression.Convert(value, d);
        if (s.IsEnum && TypeHelpers.IsNumericLike(d)) return Expression.Convert(value, d);
        if (d.IsEnum && TypeHelpers.IsNumericLike(s)) return Expression.Convert(value, d);
        if (s.IsEnum && d.IsEnum) return Expression.Convert(value, d);

        throw new NotSupportedException(
            $"ProjectTo: cannot translate conversion {TypeHelpers.Pretty(s)} -> {TypeHelpers.Pretty(d)}. Add a map or use an expression MapFrom.");
    }

    // ------------------------------------------------------------------ parameters

    /// <summary>
    /// Replaces closure variables referenced by a projection (e.g. <c>MapFrom(s =&gt; s.Name + currentUser)</c>) with
    /// properties of <paramref name="parameters"/> that have the same name, so each query can supply its own values.
    /// </summary>
    public static LambdaExpression Parameterize(LambdaExpression lambda, object parameters) =>
        (LambdaExpression)new ParameterVisitor(parameters).Visit(lambda);

    private sealed class ParameterVisitor : ExpressionVisitor
    {
        private readonly object _parameters;
        private readonly IDictionary? _dictionary;

        public ParameterVisitor(object parameters)
        {
            _parameters = parameters;
            _dictionary = parameters as IDictionary;
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression is ConstantExpression { Value: { } closure } && IsClosure(closure.GetType()))
            {
                var name = node.Member.Name;
                if (_dictionary != null)
                {
                    if (_dictionary.Contains(name))
                        return Expression.Constant(_dictionary[name], node.Type);
                }
                else if (_parameters.GetType().GetProperty(name) is { } property && property.CanRead)
                {
                    Expression access = Expression.Property(Expression.Constant(_parameters), property);
                    return access.Type == node.Type ? access : Expression.Convert(access, node.Type);
                }
            }
            return base.VisitMember(node);
        }

        private static bool IsClosure(Type type) =>
            type.IsDefined(typeof(CompilerGeneratedAttribute), false) && !type.Name.Contains("AnonymousType", StringComparison.Ordinal);
    }
}
