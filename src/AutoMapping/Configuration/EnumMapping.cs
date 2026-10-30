namespace AutoMapping;

/// <summary>Options for <see cref="EnumMappingExtensions.ConvertUsingEnumMapping{TSource,TDestination}"/>.</summary>
public interface IEnumMappingOptions<TSource, TDestination> where TSource : struct, Enum where TDestination : struct, Enum
{
    /// <summary>Match values by their numeric value.</summary>
    IEnumMappingOptions<TSource, TDestination> MapByValue();
    /// <summary>Match values by name, ignoring case. This is the default.</summary>
    IEnumMappingOptions<TSource, TDestination> MapByName();
    /// <summary>Maps one source value to one destination value, overriding the by-name/by-value match.</summary>
    IEnumMappingOptions<TSource, TDestination> MapValue(TSource sourceValue, TDestination destinationValue);
}

/// <summary>Enum to enum maps with an explicit value table (the AutoMapper.Extensions.EnumMapping feature set).</summary>
public static class EnumMappingExtensions
{
    /// <summary>
    /// Converts the enum by an explicit table built from the options. A source value without a destination throws a
    /// <see cref="MappingException"/> when mapped, and is reported by <c>AssertConfigurationIsValid</c> after
    /// <c>EnableEnumMappingValidation()</c>.
    /// </summary>
    public static IMappingExpression<TSource, TDestination> ConvertUsingEnumMapping<TSource, TDestination>(
        this IMappingExpression<TSource, TDestination> map, Action<IEnumMappingOptions<TSource, TDestination>>? options = null)
        where TSource : struct, Enum where TDestination : struct, Enum
    {
        ArgumentNullException.ThrowIfNull(map);
        var configured = new Options<TSource, TDestination>();
        options?.Invoke(configured);

        var table = new Dictionary<TSource, TDestination>();
        var unmapped = new List<string>();
        foreach (var value in Enum.GetValues<TSource>())
        {
            if (configured.Explicit.TryGetValue(value, out var explicitValue)) { table[value] = explicitValue; continue; }

            if (configured.ByValue)
            {
                var number = Convert.ToInt64(value);
                var match = Enum.GetValues<TDestination>().Cast<TDestination?>().FirstOrDefault(d => Convert.ToInt64(d!.Value) == number);
                if (match.HasValue) { table[value] = match.Value; continue; }
            }
            else if (Enum.TryParse<TDestination>(value.ToString(), ignoreCase: true, out var byName) && Enum.IsDefined(byName))
            {
                table[value] = byName;
                continue;
            }
            unmapped.Add(value.ToString());
        }

        var typeMap = ((MappingExpression<TSource, TDestination>)map).TypeMap;
        typeMap.EnumUnmapped.Clear();
        typeMap.EnumUnmapped.AddRange(unmapped);

        return map.ConvertUsing(new Func<TSource, TDestination, ResolutionContext, TDestination>((source, _, _) =>
            table.TryGetValue(source, out var result)
                ? result
                : throw new MappingException($"Enum value {typeof(TSource).Name}.{source} has no mapping to {typeof(TDestination).Name}.", typeof(TSource), typeof(TDestination))));
    }

    private sealed class Options<TSource, TDestination> : IEnumMappingOptions<TSource, TDestination>
        where TSource : struct, Enum where TDestination : struct, Enum
    {
        public bool ByValue { get; private set; }
        public Dictionary<TSource, TDestination> Explicit { get; } = new();

        public IEnumMappingOptions<TSource, TDestination> MapByValue() { ByValue = true; return this; }
        public IEnumMappingOptions<TSource, TDestination> MapByName() { ByValue = false; return this; }

        public IEnumMappingOptions<TSource, TDestination> MapValue(TSource sourceValue, TDestination destinationValue)
        {
            Explicit[sourceValue] = destinationValue;
            return this;
        }
    }
}
