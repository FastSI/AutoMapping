using System.Linq.Expressions;
using System.Reflection;
using AutoMapping.Internal;

namespace AutoMapping.Internal;

/// <summary>Dictionary&lt;string, V&gt; to object ("bag to POCO") plan: destination members by normalized name, with compiled setters.</summary>
public sealed class DictionaryToObjectPlan<TDestination>
{
    private readonly Dictionary<string, (Type Type, Action<TDestination, object?> Set)> _members = new(StringComparer.Ordinal);
    private readonly MapperConfiguration _cfg;

    internal DictionaryToObjectPlan(MapperConfiguration cfg, MemberMatcher matcher)
    {
        _cfg = cfg;
        foreach (var m in matcher.GetDestinationMembers(typeof(TDestination)))
        {
            if (m.IgnoreAttribute || m.GetOnlyCollection) continue;
            // A dictionary is typically untrusted input (JSON bag, form data): never let keys reach private/internal setters
            // ("mass assignment"). Object-to-object maps keep AutoMapper's behaviour; only the bag-to-object plan is stricter.
            if (m.Member is PropertyInfo { SetMethod.IsPublic: false }) continue;
            var target = Expression.Parameter(typeof(TDestination), "t");
            var value = Expression.Parameter(typeof(object), "v");
            var assign = Expression.Assign(Expression.MakeMemberAccess(target, m.Member), Expression.Convert(value, m.Type));
            _members[matcher.Normalize(m.Name)] = (m.Type, Expression.Lambda<Action<TDestination, object?>>(assign, target, value).Compile());
        }
    }

    public TDestination Map<TValue>(IEnumerable<KeyValuePair<string, TValue>>? source, TDestination? existing, ResolutionContext ctx)
    {
        if (source is null) return existing!;
        var result = existing ?? (TDestination)ctx.CreateInstanceOrDefault(typeof(TDestination));
        foreach (var (key, raw) in source)
        {
            if (key is null || !_members.TryGetValue(MemberMatcher.NormalizeRaw(key), out var member)) continue;
            member.Set(result, Convert(raw, member.Type, ctx));
        }
        return result;
    }

    private object? Convert(object? value, Type target, ResolutionContext ctx)
    {
        if (value is null) return target.IsValueType && Nullable.GetUnderlyingType(target) == null ? Activator.CreateInstance(target) : null;
        if (target.IsInstanceOfType(value) && TypeHelpers.IsLeaf(target)) return value;
        return _cfg.GetMapping(value.GetType(), target).MapObject(value, null, ctx);
    }
}

/// <summary>Object to Dictionary&lt;string, V&gt; plan: readable public properties and fields with compiled getters.</summary>
public sealed class ObjectToDictionaryPlan<TSource>
{
    private readonly List<(string Name, Func<TSource, object?> Get)> _members = new();
    private readonly MapperConfiguration _cfg;

    internal ObjectToDictionaryPlan(MapperConfiguration cfg, MemberMatcher matcher)
    {
        _cfg = cfg;
        foreach (var (name, member) in matcher.GetCandidateMembers(typeof(TSource)))
        {
            var source = Expression.Parameter(typeof(TSource), "s");
            var body = Expression.Convert(Expression.MakeMemberAccess(source, member), typeof(object));
            _members.Add((name, Expression.Lambda<Func<TSource, object?>>(body, source).Compile()));
        }
    }

    public Dictionary<string, TValue>? Map<TValue>(TSource? source, bool allowNull, ResolutionContext ctx)
    {
        if (source is null) return allowNull ? null : new Dictionary<string, TValue>();
        var result = new Dictionary<string, TValue>(_members.Count, StringComparer.Ordinal);
        foreach (var (name, get) in _members)
        {
            var value = get(source);
            if (value is null) { result[name] = default!; continue; }
            result[name] = value is TValue typed && (typeof(TValue) == typeof(object) || TypeHelpers.IsLeaf(value.GetType()))
                ? typed
                : (TValue)_cfg.GetMapping(value.GetType(), typeof(TValue)).MapObject(value, null, ctx)!;
        }
        return result;
    }
}
