using System.Collections.ObjectModel;
using System.Linq.Expressions;

namespace AutoMapping.Internal;

internal static class TypeHelpers
{
    public static bool IsNullableValueType(Type t) => Nullable.GetUnderlyingType(t) != null;

    public static bool IsNumericLike(Type t) => Type.GetTypeCode(t) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
        TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal or TypeCode.Char
        && !t.IsEnum;

    public static bool IsConvertiblePrimitive(Type t) => !t.IsEnum && Type.GetTypeCode(t) is
        not (TypeCode.Object or TypeCode.DBNull or TypeCode.Empty);

    /// <summary>Immutable value-like types that never need a deep copy.</summary>
    public static bool IsImmutableLeaf(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(Guid) ||
        t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(TimeSpan) ||
        t == typeof(DateOnly) || t == typeof(TimeOnly);

    public static bool IsLeaf(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return IsImmutableLeaf(t);
    }

    /// <summary>True when <paramref name="type"/> is an enumerable (other than string) with a single element type.</summary>
    public static bool TryGetEnumerableElement(Type type, out Type element)
    {
        element = null!;
        if (type == typeof(string)) return false;
        if (type.IsArray)
        {
            element = type.GetElementType()!;
            return type.GetArrayRank() == 1;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            element = type.GetGenericArguments()[0];
            return true;
        }
        foreach (var i in type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                element = i.GetGenericArguments()[0];
                return true;
            }
        }
        return false;
    }

    public static bool IsCollectionLike(Type type) => TryGetEnumerableElement(type, out _);

    /// <summary>Source side: anything enumerable of KeyValuePair.</summary>
    public static bool TryGetKeyValueElement(Type type, out Type key, out Type value)
    {
        key = value = null!;
        if (!TryGetEnumerableElement(type, out var element)) return false;
        if (!element.IsGenericType || element.GetGenericTypeDefinition() != typeof(KeyValuePair<,>)) return false;
        var args = element.GetGenericArguments();
        key = args[0];
        value = args[1];
        return true;
    }

    /// <summary>Destination side: a type we can build as Dictionary&lt;K,V&gt;.</summary>
    public static bool IsBuildableDictionary(Type type, out Type key, out Type value)
    {
        key = value = null!;
        if (!type.IsGenericType) return false;
        var def = type.GetGenericTypeDefinition();
        if (def != typeof(Dictionary<,>) && def != typeof(IDictionary<,>) && def != typeof(IReadOnlyDictionary<,>)) return false;
        var args = type.GetGenericArguments();
        key = args[0];
        value = args[1];
        return true;
    }

    public static Type? FindGenericInterface(Type type, Type openDefinition)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == openDefinition) return type;
        return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == openDefinition);
    }

    public static int InheritanceDepth(Type t)
    {
        var depth = 0;
        for (var b = t.BaseType; b != null; b = b.BaseType) depth++;
        return depth;
    }

    public static Expression IsNull(Expression e)
    {
        if (IsNullableValueType(e.Type)) return Expression.Not(Expression.Property(e, "HasValue"));
        return Expression.ReferenceEqual(e, Expression.Constant(null, e.Type));
    }

    public static bool CanBeNull(Type t) => !t.IsValueType || IsNullableValueType(t);

    public static string Pretty(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        var name = t.Name[..t.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
    }
}

internal sealed class ParameterReplacer : ExpressionVisitor
{
    private readonly ParameterExpression _from;
    private readonly Expression _to;

    private ParameterReplacer(ParameterExpression from, Expression to)
    {
        _from = from;
        _to = to;
    }

    public static Expression Replace(Expression body, ParameterExpression from, Expression to)
    {
        // allow base-typed lambdas (inherited configuration) to run against a derived source
        var target = to.Type == from.Type ? to : Expression.Convert(to, from.Type);
        return new ParameterReplacer(from, target).Visit(body);
    }

    protected override Expression VisitParameter(ParameterExpression node) => node == _from ? _to : base.VisitParameter(node);
}
