using System.Collections.Specialized;
using System.Linq.Expressions;
using System.Net;
using System.Reflection;

namespace AutoMapping.OData;

/// <summary>Raised for malformed or disallowed query options. Safe to turn into a 400 response.</summary>
public sealed class ODataException : AutoMappingException
{
    public ODataException(string message) : base(message) { }
}

/// <summary>Limits applied to untrusted query strings.</summary>
public sealed class ODataSettings
{
    /// <summary>Largest <c>$top</c> accepted (and the page size when none is given). Default 1000.</summary>
    public int MaxTop { get; set; } = 1000;
    public int MaxExpressionLength { get; set; } = 4000;
    public int MaxExpressionDepth { get; set; } = 32;
    /// <summary>Largest <c>$skip</c> accepted. Deep offsets are slow on big tables; page by a key instead. Default 100000.</summary>
    public int MaxSkip { get; set; } = 100_000;
    /// <summary>Most items in <c>$orderby</c>, <c>$select</c> and <c>$expand</c> (each). Default 16.</summary>
    public int MaxListItems { get; set; } = 16;
    /// <summary>Most words in <c>$search</c>. Every word scans every string column, so this bounds the query cost. Default 8.</summary>
    public int MaxSearchTerms { get; set; } = 8;
    /// <summary>Shortest <c>$search</c> word or phrase accepted. One or two letters match almost every row and force a full scan. Default 3.</summary>
    public int MinSearchTermLength { get; set; } = 3;

    internal void CheckList(string name, string text, int items)
    {
        if (text.Length > MaxExpressionLength) throw new ODataException($"{name} is longer than {MaxExpressionLength} characters.");
        if (items > MaxListItems) throw new ODataException($"{name} has more than {MaxListItems} items.");
    }
}

/// <summary>The supported OData system query options.</summary>
public sealed class ODataQueryOptions
{
    public string? Filter { get; set; }
    public string? Search { get; set; }
    public string? OrderBy { get; set; }
    public string? Select { get; set; }
    public string? Expand { get; set; }
    public int? Top { get; set; }
    public int? Skip { get; set; }
    public bool Count { get; set; }

    /// <summary>Parses <c>$filter=...&amp;$top=10</c> (a leading '?' and URL encoding are accepted).</summary>
    public static ODataQueryOptions Parse(string? queryString)
    {
        var options = new ODataQueryOptions();
        if (string.IsNullOrWhiteSpace(queryString)) return options;
        foreach (var pair in queryString.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = WebUtility.UrlDecode(eq < 0 ? pair : pair[..eq]).Trim().ToLowerInvariant();
            var value = eq < 0 ? "" : WebUtility.UrlDecode(pair[(eq + 1)..]);
            switch (key)
            {
                case "$filter": options.Filter = value; break;
                case "$search": options.Search = value; break;
                case "$orderby": options.OrderBy = value; break;
                case "$select": options.Select = value; break;
                case "$expand": options.Expand = value; break;
                case "$top": options.Top = Number(key, value); break;
                case "$skip": options.Skip = Number(key, value); break;
                case "$count": options.Count = value.Equals("true", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        return options;
    }

    private static int Number(string name, string value) =>
        int.TryParse(value, out var n) && n >= 0 ? n : throw new ODataException($"{name} must be a non-negative integer.");
}

public sealed class ODataResult<TDestination>
{
    public ODataResult(IReadOnlyList<TDestination> items, long? count, IReadOnlyList<IReadOnlyDictionary<string, object?>>? shaped)
    {
        Items = items;
        Count = count;
        Shaped = shaped;
    }

    /// <summary>The page of DTOs. When <c>$select</c> was used, prefer <see cref="Shaped"/>.</summary>
    public IReadOnlyList<TDestination> Items { get; }
    /// <summary>Total matching rows before paging, when <c>$count=true</c>.</summary>
    public long? Count { get; }
    /// <summary>Only the <c>$select</c>ed properties of each row; null when no <c>$select</c> was given.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? Shaped { get; }
}

/// <summary>
/// OData query support for DTO endpoints over entity queries (the role of AutoMapper.AspNetCore.OData.EFCore). The client talks
/// in DTO terms; <c>$filter</c> and <c>$orderby</c> are translated to the entity through the map, so they run in the database.
/// Supported: <c>$filter</c> (eq ne gt ge lt le and or not, add sub mul div mod, contains startswith endswith tolower toupper
/// trim length concat indexof substring year month day hour minute second round floor ceiling), <c>$orderby</c>, <c>$top</c>,
/// <c>$skip</c>, <c>$count</c>, <c>$search</c>, <c>any()/all()</c>, <c>$select</c> (top-level properties) and <c>$expand</c> (members marked ExplicitExpansion).
/// It does not use the Microsoft.AspNetCore.OData package, so $apply and <c>$select</c>/<c>$expand</c> with nested
/// options are not supported.
/// </summary>
public static class ODataExtensions
{
    /// <summary>Filters the entity query by <c>$filter</c> only (handy for your own counting or paging).</summary>
    public static IQueryable<TSource> ApplyFilter<TSource, TDestination>(this IQueryable<TSource> source, IMapper mapper,
        ODataQueryOptions options, ODataSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        settings ??= new ODataSettings();
        var config = Config(mapper);
        if (!string.IsNullOrWhiteSpace(options.Filter))
        {
            var onDto = ODataFilterParser.ParseFilter(options.Filter, typeof(TDestination), settings);
            source = source.Where((Expression<Func<TSource, bool>>)ExpressionMappingExtensions.MapLambda(config, typeof(TSource), typeof(TDestination), onDto));
        }
        if (!string.IsNullOrWhiteSpace(options.Search) && ODataFilterParser.ParseSearch(options.Search, typeof(TDestination), settings) is { } search)
            source = source.Where((Expression<Func<TSource, bool>>)ExpressionMappingExtensions.MapLambda(config, typeof(TSource), typeof(TDestination), search));
        return source;
    }

    /// <summary>Applies filter, ordering, paging and the projection to <typeparamref name="TDestination"/>. Nothing is executed.</summary>
    public static IQueryable<TDestination> GetQuery<TSource, TDestination>(this IQueryable<TSource> source, IMapper mapper,
        ODataQueryOptions options, ODataSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        settings ??= new ODataSettings();
        var config = Config(mapper);
        var filtered = source.ApplyFilter<TSource, TDestination>(mapper, options, settings);
        var ordered = Order<TSource, TDestination>(filtered, config, options, settings);

        var skip = options.Skip ?? 0;
        if (skip > settings.MaxSkip) throw new ODataException($"$skip cannot be larger than {settings.MaxSkip}.");
        var top = Math.Min(options.Top ?? settings.MaxTop, settings.MaxTop);
        var paged = ordered.Skip(skip).Take(top);

        ISet<string>? expand = null;
        if (!string.IsNullOrWhiteSpace(options.Expand))
        {
            var parts = options.Expand.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            settings.CheckList("$expand", options.Expand, parts.Length);
            expand = new HashSet<string>(parts.Select(p => ResolvePath(typeof(TDestination), p)), StringComparer.Ordinal);
        }

        var lambda = config.GetProjection(typeof(TSource), typeof(TDestination), expand);
        return paged.Select((Expression<Func<TSource, TDestination>>)lambda);
    }

    /// <summary>Runs the query and returns the page, the optional total count and the optional <c>$select</c> shape.</summary>
    public static ODataResult<TDestination> Get<TSource, TDestination>(this IQueryable<TSource> source, IMapper mapper,
        ODataQueryOptions options, ODataSettings? settings = null)
    {
        settings ??= new ODataSettings();
        long? count = options.Count ? source.ApplyFilter<TSource, TDestination>(mapper, options, settings).LongCount() : null;
        var items = source.GetQuery<TSource, TDestination>(mapper, options, settings).ToList();

        IReadOnlyList<IReadOnlyDictionary<string, object?>>? shaped = null;
        if (!string.IsNullOrWhiteSpace(options.Select))
        {
            var names = options.Select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            settings.CheckList("$select", options.Select, names.Length);
            var props = names
                .Select(n => typeof(TDestination).GetProperty(n, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                             ?? throw new ODataException($"'{n}' is not a property of {typeof(TDestination).Name}.")).ToArray();
            shaped = items.Select(i => (IReadOnlyDictionary<string, object?>)props.ToDictionary(p => p.Name, p => p.GetValue(i))).ToList();
        }
        return new ODataResult<TDestination>(items, count, shaped);
    }

    private static MapperConfiguration Config(IMapper mapper) =>
        (mapper ?? throw new ArgumentNullException(nameof(mapper))).ConfigurationProvider as MapperConfiguration
        ?? throw new ArgumentException("OData support needs a AutoMapping mapper.", nameof(mapper));

    private static IQueryable<TSource> Order<TSource, TDestination>(IQueryable<TSource> query, MapperConfiguration config,
        ODataQueryOptions options, ODataSettings settings)
    {
        if (string.IsNullOrWhiteSpace(options.OrderBy)) return query;
        var first = true;
        foreach (var (selector, descending) in ODataFilterParser.ParseOrderBy(options.OrderBy, typeof(TDestination), settings))
        {
            var onEntity = ExpressionMappingExtensions.MapLambda(config, typeof(TSource), typeof(TDestination), selector);
            var method = descending
                ? (first ? nameof(Queryable.OrderByDescending) : nameof(Queryable.ThenByDescending))
                : (first ? nameof(Queryable.OrderBy) : nameof(Queryable.ThenBy));
            var call = Expression.Call(typeof(Queryable), method, new[] { typeof(TSource), onEntity.Body.Type },
                query.Expression, Expression.Quote(onEntity));
            query = query.Provider.CreateQuery<TSource>(call);
            first = false;
        }
        return query;
    }

    // "customer/address" -> "Customer.Address" using the real member names, as ProjectionBuilder expects
    private static string ResolvePath(Type type, string path)
    {
        var names = new List<string>();
        foreach (var segment in path.Split('/', '.'))
        {
            var p = type.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                    ?? throw new ODataException($"'{segment}' is not a member of {type.Name}.");
            names.Add(p.Name);
            type = TypeHelpers_Element(p.PropertyType);
        }
        return string.Join('.', names);
    }

    private static Type TypeHelpers_Element(Type t) =>
        t != typeof(string) && t.GetInterfaces().Concat(new[] { t }).FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } e
            ? e.GetGenericArguments()[0] : t;
}
