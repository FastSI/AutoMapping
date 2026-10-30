using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace AutoMapping.Internal;

/// <summary>
/// Name based source-member discovery for one profile: exact (case/separator-insensitive), prefixes/postfixes,
/// name replacements, parameterless methods and flattening. Instances are immutable and cached per configuration.
/// </summary>
internal sealed class MemberMatcher
{
    private const int MaxFlattenDepth = 5;
    private static readonly string[] NeverMethods = { "ToString", "GetHashCode", "GetType", "MemberwiseClone", "Finalize", "Clone", "Deconstruct" };

    private readonly string[] _sourcePrefixes;
    private readonly string[] _sourcePostfixes;
    private readonly string[] _destPrefixes;
    private readonly string[] _destPostfixes;
    private readonly (string From, string To)[] _replacements;
    private readonly Func<PropertyInfo, bool> _shouldMapProperty;
    private readonly Func<FieldInfo, bool> _shouldMapField;
    private readonly Func<MethodInfo, bool> _shouldMapMethod;
    private readonly INamingConvention? _destConvention;
    private readonly INamingConvention? _sourceConvention;
    private readonly (string Original, string Alias)[] _aliases;
    private readonly ConcurrentDictionary<Type, Dictionary<string, MemberInfo>> _sourceCache = new();

    public MemberMatcher(
        IEnumerable<string> sourcePrefixes, IEnumerable<string> sourcePostfixes,
        IEnumerable<string> destPrefixes, IEnumerable<string> destPostfixes,
        IEnumerable<(string From, string To)> replacements,
        Func<PropertyInfo, bool>? shouldMapProperty, Func<FieldInfo, bool>? shouldMapField, Func<MethodInfo, bool>? shouldMapMethod,
        INamingConvention? destConvention, INamingConvention? sourceConvention = null,
        IEnumerable<(string Original, string Alias)>? aliases = null)
    {
        _aliases = aliases?.ToArray() ?? Array.Empty<(string, string)>();
        _sourceConvention = sourceConvention;
        _sourcePrefixes = sourcePrefixes.Where(p => p.Length > 0).Distinct().ToArray();
        _sourcePostfixes = sourcePostfixes.Where(p => p.Length > 0).Distinct().ToArray();
        _destPrefixes = destPrefixes.Where(p => p.Length > 0).Distinct().ToArray();
        _destPostfixes = destPostfixes.Where(p => p.Length > 0).Distinct().ToArray();
        _replacements = replacements.ToArray();
        _shouldMapProperty = shouldMapProperty ?? (p => p.GetMethod?.IsPublic == true || p.SetMethod?.IsPublic == true);
        _shouldMapField = shouldMapField ?? (f => f.IsPublic);
        _shouldMapMethod = shouldMapMethod ?? (m => !m.IsSpecialName);
        _destConvention = destConvention;
    }

    // ------------------------------------------------------------------ names

    /// <summary>Normalizes a destination name (or any name that is not a source member).</summary>
    public string Normalize(string name) => NormalizeRaw(name);

    /// <summary>Normalizes a source member name, applying <c>ReplaceMemberName</c> replacements.</summary>
    private string NormalizeSource(string name)
    {
        foreach (var (from, to) in _replacements)
            if (from.Length > 0) name = name.Replace(from, to, StringComparison.Ordinal);
        if (_sourceConvention != null)
        {
            // SourceMemberNamingConvention: the convention decides where words start and end; the key is the words glued together
            var words = _sourceConvention.SplittingExpression.Matches(name).Select(m => m.Value).Where(v => v.Length > 0);
            var glued = string.Concat(words);
            if (glued.Length > 0) name = glued;
        }
        return NormalizeRaw(name);
    }

    public static string NormalizeRaw(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            if (c != '_' && c != '-' && c != ' ') sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Splits PascalCase / camelCase / snake_case into words.</summary>
    public static List<string> SplitWords(string name)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_' || c == '-' || c == ' ')
            {
                Flush();
                continue;
            }
            if (sb.Length > 0 && char.IsUpper(c))
            {
                var prevLower = char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]);
                var nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (prevLower || (char.IsUpper(name[i - 1]) && nextLower)) Flush();
            }
            sb.Append(c);
        }
        Flush();
        return words;

        void Flush()
        {
            if (sb.Length == 0) return;
            words.Add(sb.ToString());
            sb.Clear();
        }
    }

    private List<string> Words(string name) =>
        _destConvention != null
            ? _destConvention.SplittingExpression.Matches(name).Select(m => m.Value).Where(v => v.Length > 0).ToList()
            : SplitWords(name);

    public static Type MemberType(MemberInfo m) => m switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        MethodInfo mi => mi.ReturnType,
        _ => throw new ArgumentException("Unsupported member " + m)
    };

    // ------------------------------------------------------------------ source side

    private Dictionary<string, MemberInfo> GetSourceMembers(Type type) =>
        _sourceCache.GetOrAdd(type, t =>
        {
            var map = new Dictionary<string, MemberInfo>(StringComparer.Ordinal);
            var members = new List<(string Name, MemberInfo Member)>();

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (p.CanRead && p.GetMethod!.IsPublic && p.GetIndexParameters().Length == 0 && _shouldMapProperty(p)) members.Add((p.Name, p));
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (_shouldMapField(f)) members.Add((f.Name, f));
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName || m.GetParameters().Length != 0 || m.ReturnType == typeof(void) || m.IsGenericMethodDefinition) continue;
                if (m.DeclaringType == typeof(object) || Array.IndexOf(NeverMethods, m.Name) >= 0) continue;
                if (_shouldMapMethod(m)) members.Add((m.Name, m));
            }

            // exact names first so they always win over prefix/postfix variants
            foreach (var (name, member) in members) map.TryAdd(NormalizeSource(name), member);
            foreach (var (original, alias) in _aliases)
                foreach (var (name, member) in members)
                    if (string.Equals(name, original, StringComparison.OrdinalIgnoreCase)) map.TryAdd(NormalizeSource(alias), member);
            foreach (var (name, member) in members)
            {
                foreach (var prefix in _sourcePrefixes)
                    if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        map.TryAdd(NormalizeSource(name[prefix.Length..]), member);
                foreach (var postfix in _sourcePostfixes)
                    if (name.Length > postfix.Length && name.EndsWith(postfix, StringComparison.OrdinalIgnoreCase))
                        map.TryAdd(NormalizeSource(name[..^postfix.Length]), member);
            }
            return map;
        });

    /// <summary>Source members (top level) that may be consumed; used for <see cref="MemberList.Source"/> validation.</summary>
    public IEnumerable<MemberInfo> GetSourceMemberList(Type type) =>
        GetSourceMembers(type).Values.Distinct();

    /// <summary>Top-level readable properties/fields only (candidates for ML matching).</summary>
    public IEnumerable<(string Name, MemberInfo Member)> GetCandidateMembers(Type type)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && p.GetMethod!.IsPublic && p.GetIndexParameters().Length == 0 && _shouldMapProperty(p)) yield return (p.Name, p);
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (_shouldMapField(f)) yield return (f.Name, f);
    }

    /// <summary>Resolves a destination name to a chain of source members (direct, then flattened), trying prefix/postfix-stripped names too.</summary>
    public IReadOnlyList<MemberInfo>? Resolve(string destinationName, Type sourceType)
    {
        foreach (var candidate in DestinationCandidates(destinationName))
        {
            var chain = ResolveCore(candidate, sourceType, 0);
            if (chain != null) return chain;
        }
        return null;
    }

    private IEnumerable<string> DestinationCandidates(string name)
    {
        yield return name;
        foreach (var prefix in _destPrefixes)
            if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return name[prefix.Length..];
        foreach (var postfix in _destPostfixes)
            if (name.Length > postfix.Length && name.EndsWith(postfix, StringComparison.OrdinalIgnoreCase))
                yield return name[..^postfix.Length];
    }

    private IReadOnlyList<MemberInfo>? ResolveCore(string name, Type sourceType, int depth)
    {
        if (depth > MaxFlattenDepth) return null;
        var members = GetSourceMembers(sourceType);

        if (members.TryGetValue(Normalize(name), out var direct))
            return new[] { direct };

        // flattening: AddressCity -> Address.City (try every word boundary)
        var words = Words(name);
        for (var i = 1; i < words.Count; i++)
        {
            var head = Normalize(string.Concat(words.Take(i)));
            if (!members.TryGetValue(head, out var first)) continue;

            var firstType = MemberType(first);
            if (!CanFlattenInto(firstType)) continue;

            var rest = string.Concat(words.Skip(i));
            var tail = ResolveCore(rest, firstType, depth + 1);
            if (tail != null) return new[] { first }.Concat(tail).ToArray();
        }
        return null;
    }

    private static bool CanFlattenInto(Type t) =>
        !t.IsPrimitive && !t.IsEnum && t != typeof(string) && t != typeof(decimal) && !t.IsPointer && t != typeof(object);

    // ------------------------------------------------------------------ destination side

    public readonly record struct DestinationMember(string Name, Type Type, MemberInfo Member, bool IgnoreAttribute, bool GetOnlyCollection);

    public IEnumerable<DestinationMember> GetDestinationMembers(Type type)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0 || !_shouldMapProperty(p)) continue;
            var ignore = p.IsDefined(typeof(IgnoreMapAttribute), true);
            if (p.SetMethod != null)   // public, init-only, internal, protected and private setters (compiled trees can call them)
            {
                yield return new DestinationMember(p.Name, p.PropertyType, p, ignore, false);
            }
            else if (p.GetMethod is { IsPublic: true } && IsPopulatable(p.PropertyType))
            {
                // get-only collections/dictionaries are filled in place
                yield return new DestinationMember(p.Name, p.PropertyType, p, ignore, true);
            }
        }
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (f.IsInitOnly || f.IsLiteral || !_shouldMapField(f)) continue;
            yield return new DestinationMember(f.Name, f.FieldType, f, f.IsDefined(typeof(IgnoreMapAttribute), true), false);
        }
    }

    public static bool IsPopulatable(Type t) =>
        t != typeof(string) && !t.IsArray &&
        (ImplementsGeneric(t, typeof(ICollection<>)) || ImplementsGeneric(t, typeof(IDictionary<,>)));

    private static bool ImplementsGeneric(Type t, Type definition) =>
        (t.IsGenericType && t.GetGenericTypeDefinition() == definition) ||
        t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == definition);
}
