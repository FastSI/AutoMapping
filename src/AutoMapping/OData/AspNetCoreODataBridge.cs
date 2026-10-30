using System.Reflection;

namespace AutoMapping.OData;

/// <summary>
/// Lets controllers keep taking the official <c>Microsoft.AspNetCore.OData</c> <c>ODataQueryOptions&lt;TDto&gt;</c> (model binding,
/// routing, EDM) while AutoMapping translates the DTO-level query onto the entity query. AutoMapping does <b>not</b> reference that
/// package: the options object is read by reflection (its <c>RawValues</c>), so the package is only needed in your own web project
/// and nothing is added to apps that don't use OData. The raw query-string values are handed to AutoMapping's engine, so the supported
/// syntax is the one documented in the README.
/// </summary>
public static class AspNetCoreODataBridge
{
    private const string OptionsTypeName = "Microsoft.AspNetCore.OData.Query.ODataQueryOptions";

    /// <summary>Converts an official <c>ODataQueryOptions</c> (or <c>ODataQueryOptions&lt;T&gt;</c>) to AutoMapping's options.</summary>
    /// <exception cref="ArgumentException"><paramref name="options"/> is not an ASP.NET Core OData query-options object.</exception>
    public static ODataQueryOptions ToAutoMapping(object options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsAspNetOptions(options.GetType()))
            throw new ArgumentException($"Expected Microsoft.AspNetCore.OData ODataQueryOptions but got {options.GetType().FullName}.", nameof(options));

        var raw = options.GetType().GetProperty("RawValues", BindingFlags.Public | BindingFlags.Instance)?.GetValue(options)
                  ?? throw new NotSupportedException("The installed Microsoft.AspNetCore.OData version does not expose ODataQueryOptions.RawValues.");

        if (!string.IsNullOrWhiteSpace(Raw(raw, "Apply")))
            throw new NotSupportedException("$apply is not supported by AutoMapping.");

        return new ODataQueryOptions
        {
            Filter = Blank(Raw(raw, "Filter")),
            OrderBy = Blank(Raw(raw, "OrderBy")),
            Search = Blank(Raw(raw, "Search")),
            Select = Blank(Raw(raw, "Select")),
            Expand = Blank(Raw(raw, "Expand")),
            Top = Number("$top", Raw(raw, "Top")),
            Skip = Number("$skip", Raw(raw, "Skip")),
            Count = string.Equals(Raw(raw, "Count"), "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Filter, order, page and project the entity query to <typeparamref name="TDestination"/> (not executed).
    /// <paramref name="aspNetCoreOptions"/> is the official <c>ODataQueryOptions</c>.</summary>
    public static IQueryable<TDestination> GetQuery<TSource, TDestination>(this IQueryable<TSource> source, IMapper mapper,
        object aspNetCoreOptions, ODataSettings? settings = null) =>
        source.GetQuery<TSource, TDestination>(mapper, ToAutoMapping(aspNetCoreOptions), settings);

    /// <summary>Runs the query: the page, the optional total count and the optional <c>$select</c> shape.</summary>
    public static ODataResult<TDestination> Get<TSource, TDestination>(this IQueryable<TSource> source, IMapper mapper,
        object aspNetCoreOptions, ODataSettings? settings = null) =>
        source.Get<TSource, TDestination>(mapper, ToAutoMapping(aspNetCoreOptions), settings);

    private static bool IsAspNetOptions(Type? type)
    {
        for (; type is not null; type = type.BaseType)
            if (type.FullName == OptionsTypeName) return true;
        return false;
    }

    private static string? Raw(object raw, string property) =>
        raw.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(raw) as string;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? Number(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : int.TryParse(value, out var n) && n >= 0 ? n
        : throw new ODataException($"{name} must be a non-negative integer.");
}
