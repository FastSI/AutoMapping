using System.Linq.Expressions;
using System.Reflection;

namespace AutoMapping;

/// <summary>Collection equivalency by key (AutoMapper.Collection's <c>UseEntityFrameworkCoreModel</c> and friends).</summary>
public static class KeyEqualityExtensions
{
    /// <summary>
    /// Two objects are "the same" when the named members are equal. Each key must exist on both types (matched ignoring case).
    /// Example: <c>CreateMap&lt;ProductDto, Product&gt;().EqualityComparisonByKeys("Id")</c>.
    /// </summary>
    public static IMappingExpression<TSource, TDestination> EqualityComparisonByKeys<TSource, TDestination>(
        this IMappingExpression<TSource, TDestination> map, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(map);
        var predicate = Build(typeof(TSource), typeof(TDestination), keys)
                        ?? throw new MappingConfigurationException(
                            $"{typeof(TSource).Name} -> {typeof(TDestination).Name}: key member(s) {string.Join(", ", keys)} not found on both types.");
        return map.EqualityComparison((Expression<Func<TSource, TDestination, bool>>)predicate);
    }

    /// <summary>
    /// Derives <c>EqualityComparison</c> for every map whose destination is an entity of the given EF Core model from that
    /// entity's primary key. Pass a <c>DbContext</c> or its <c>Model</c>. AutoMapping has no EF Core dependency: the model is
    /// read through its public members (<c>GetEntityTypes</c>, <c>ClrType</c>, <c>FindPrimaryKey</c>, <c>Properties</c>, <c>Name</c>).
    /// Maps that already have an explicit <c>EqualityComparison</c>, or whose source lacks a key member, are left alone.
    /// </summary>
    public static void UseEntityFrameworkCoreModel(this IMapperConfigurationExpression cfg, object dbContextOrModel)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(dbContextOrModel);
        var model = Get(dbContextOrModel, "Model") ?? dbContextOrModel;

        var keys = new Dictionary<Type, string[]>();
        foreach (var entityType in (System.Collections.IEnumerable)Call(model, "GetEntityTypes")!)
        {
            if (Get(entityType, "ClrType") is not Type clr) continue;
            var key = Call(entityType, "FindPrimaryKey");
            if (key == null) continue;
            var names = ((System.Collections.IEnumerable)Get(key, "Properties")!).Cast<object>()
                .Select(p => (string)Get(p, "Name")!).ToArray();
            if (names.Length > 0) keys[clr] = names;
        }

        cfg.ForAllMaps((info, map) =>
        {
            if (!keys.TryGetValue(info.DestinationType, out var names)) return;
            var predicate = Build(info.SourceType, info.DestinationType, names);
            if (predicate != null) map.EqualityComparison(predicate);
        });
    }

    internal static LambdaExpression? Build(Type source, Type destination, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return null;
        var s = Expression.Parameter(source, "s");
        var d = Expression.Parameter(destination, "d");
        Expression? body = null;
        foreach (var key in keys)
        {
            var sm = Find(source, key);
            var dm = Find(destination, key);
            if (sm == null || dm == null) return null;
            Expression left = Expression.MakeMemberAccess(s, sm);
            Expression right = Expression.MakeMemberAccess(d, dm);
            if (left.Type != right.Type)
            {
                var common = Nullable.GetUnderlyingType(left.Type) == right.Type ? left.Type
                    : Nullable.GetUnderlyingType(right.Type) == left.Type ? right.Type : null;
                if (common == null) return null;
                left = Expression.Convert(left, common);
                right = Expression.Convert(right, common);
            }
            var equal = Expression.Equal(left, right);
            body = body == null ? equal : Expression.AndAlso(body, equal);
        }
        return Expression.Lambda(typeof(Func<,,>).MakeGenericType(source, destination, typeof(bool)), body!, s, d);
    }

    private static MemberInfo? Find(Type type, string name) =>
        (MemberInfo?)type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase) ??
        type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    // public member lookup on the runtime type and its interfaces (EF Core exposes much of the model through interfaces)
    private static IEnumerable<Type> TypesOf(object o) => new[] { o.GetType() }.Concat(o.GetType().GetInterfaces());

    private static object? Get(object o, string name)
    {
        foreach (var t in TypesOf(o))
            if (t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { } p && p.GetIndexParameters().Length == 0)
                return p.GetValue(o);
        return null;
    }

    private static object? Call(object o, string name)
    {
        foreach (var t in TypesOf(o))
            if (t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) is { } m)
                return m.Invoke(o, null);
        throw new MappingConfigurationException($"{o.GetType().Name} has no public {name}() method; is it an EF Core model or DbContext?");
    }
}
