using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;

namespace AutoMapping.Internal;

/// <summary>Scalar conversion helpers called from compiled mappings.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class Converters
{
    private static class Cache<T>
    {
        public static readonly TypeConverter Converter = TypeDescriptor.GetConverter(typeof(T));
    }

    private static class EnumMap<TS, TD>
        where TS : struct, Enum
        where TD : struct, Enum
    {
        public static readonly Dictionary<TS, TD> Map = Build();

        private static Dictionary<TS, TD> Build()
        {
            var map = new Dictionary<TS, TD>();
            foreach (var value in Enum.GetValues<TS>())
            {
                if (Enum.TryParse<TD>(value.ToString(), true, out var byName))
                    map[value] = byName;
                else
                    map[value] = (TD)Enum.ToObject(typeof(TD), System.Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }
            return map;
        }
    }

    public static string? AnyToString<T>(T value) => value?.ToString();

    public static string EnumToString<T>(T value) where T : struct, Enum => value.ToString();

    public static T ParseEnum<T>(string? value) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value) ? default : Enum.Parse<T>(value, ignoreCase: true);

    public static TD EnumToEnum<TS, TD>(TS value) where TS : struct, Enum where TD : struct, Enum =>
        EnumMap<TS, TD>.Map.TryGetValue(value, out var mapped)
            ? mapped
            : (TD)Enum.ToObject(typeof(TD), System.Convert.ToInt64(value, CultureInfo.InvariantCulture));

    public static T? FromString<T>(string? value)
    {
        if (string.IsNullOrEmpty(value)) return default;
        return (T?)Cache<T>.Converter.ConvertFromInvariantString(value);
    }

    public static T? FromStringNullable<T>(string? value) where T : struct
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (typeof(T).IsEnum) return (T)Enum.Parse(typeof(T), value, ignoreCase: true);   // same rules as the non-nullable path
        return (T?)Cache<T>.Converter.ConvertFromInvariantString(value);
    }

    public static T ChangeType<T>(object? value)
    {
        if (value is null) return default!;
        return (T)System.Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }
}
