using AutoMapping;
using AutoMapping.OData;
using Xunit;

namespace AutoMapping.Tests;

public enum OdStatus { New, Paid }
public class OdCustomer { public string Name { get; set; } = ""; public string City { get; set; } = ""; }
public class OdLine { public string Sku { get; set; } = ""; public decimal Price { get; set; } }
public class OdLineDto { public string Sku { get; set; } = ""; public decimal Price { get; set; } }
public class OdBasket { public int Id { get; set; } public string Title { get; set; } = ""; public string Note { get; set; } = ""; public List<OdLine> Lines { get; set; } = new(); }
public class OdBasketDto { public int Id { get; set; } public string Title { get; set; } = ""; public string Note { get; set; } = ""; public List<OdLineDto> Lines { get; set; } = new(); }
public class OdOrder { public int Id { get; set; } public decimal Total { get; set; } public DateTime Placed { get; set; } public OdStatus Status { get; set; } public OdCustomer Customer { get; set; } = new(); public int? Rating { get; set; } }
public class OdOrderDto { public int Id { get; set; } public decimal Total { get; set; } public DateTime Placed { get; set; } public OdStatus Status { get; set; } public string CustomerName { get; set; } = ""; public string CustomerCity { get; set; } = ""; public int? Rating { get; set; } }

public class ODataTests
{
    private static readonly IMapper Mapper = new MapperConfiguration(c => c.CreateMap<OdOrder, OdOrderDto>()).CreateMapper();

    private static IQueryable<OdOrder> Data() => new[]
    {
        new OdOrder { Id = 1, Total = 10m,  Placed = new DateTime(2024, 1, 5),  Status = OdStatus.New,  Customer = new OdCustomer { Name = "Ann",   City = "Oslo" }, Rating = 5 },
        new OdOrder { Id = 2, Total = 99.5m, Placed = new DateTime(2024, 3, 9),  Status = OdStatus.Paid, Customer = new OdCustomer { Name = "Bob",   City = "Rome" } },
        new OdOrder { Id = 3, Total = 250m, Placed = new DateTime(2025, 6, 1),  Status = OdStatus.Paid, Customer = new OdCustomer { Name = "Anna",  City = "Oslo" }, Rating = 3 },
        new OdOrder { Id = 4, Total = 75m,  Placed = new DateTime(2025, 7, 20), Status = OdStatus.New,  Customer = new OdCustomer { Name = "O'Neil", City = "Rome" }, Rating = 4 },
    }.AsQueryable();

    private static int[] Ids(string query) =>
        Data().Get<OdOrder, OdOrderDto>(Mapper, ODataQueryOptions.Parse(query)).Items.Select(i => i.Id).ToArray();

    [Fact]
    public void Filter_comparison_logic_and_literals()
    {
        Assert.Equal(new[] { 3 }, Ids("$filter=Total gt 100"));
        Assert.Equal(new[] { 2, 4 }, Ids("$filter=Total ge 75 and Total lt 100"));
        Assert.Equal(new[] { 1, 4 }, Ids("$filter=Status eq 'New'"));
        Assert.Equal(new[] { 2 }, Ids("$filter=CustomerName eq 'Bob' or Total eq 99.5"));
        Assert.Equal(new[] { 2, 4 }, Ids("$filter=not (CustomerCity eq 'Oslo')"));
        Assert.Equal(new[] { 4 }, Ids("$filter=CustomerName eq 'O''Neil'"));
        Assert.Equal(new[] { 2, 3 }, Ids("$filter=Status eq AutoMapping.Tests.OdStatus'Paid'"));   // OData's qualified enum literal
        Assert.Equal(new[] { 3 }, Ids("$filter=Placed ge 2025-01-01T00:00:00Z and Total gt 100"));
        Assert.Equal(new[] { 3, 4 }, Ids("$filter=Placed ge 2025-01-01"));
        Assert.Equal(new[] { 2 }, Ids("$filter=Rating eq null"));
        Assert.Equal(new[] { 1, 3, 4 }, Ids("$filter=Rating ne null"));
    }

    [Fact]
    public void Filter_functions_and_arithmetic()
    {
        Assert.Equal(new[] { 1, 3 }, Ids("$filter=startswith(CustomerName,'An')"));
        Assert.Equal(new[] { 3 }, Ids("$filter=contains(tolower(CustomerName),'nna')"));
        Assert.Equal(new[] { 4 }, Ids("$filter=length(CustomerName) gt 4"));
        Assert.Equal(new[] { 3, 4 }, Ids("$filter=year(Placed) eq 2025"));
        Assert.Equal(new[] { 3 }, Ids("$filter=Total mul 2 gt 400"));
        Assert.Equal(new[] { 1, 3 }, Ids("$filter=Id mod 2 eq 1"));
    }

    [Fact]
    public void OrderBy_top_skip_and_count()
    {
        var result = Data().Get<OdOrder, OdOrderDto>(Mapper, ODataQueryOptions.Parse("?$orderby=CustomerCity,Total desc&$skip=1&$top=2&$count=true"));
        // ordered: Oslo 3(250), 1(10); Rome 2(99.5), 4(75) -> skip 1, take 2 -> 1, 2
        Assert.Equal(new[] { 1, 2 }, result.Items.Select(i => i.Id));
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void Select_shapes_rows_and_count_ignores_paging()
    {
        var result = Data().Get<OdOrder, OdOrderDto>(Mapper, ODataQueryOptions.Parse("$filter=CustomerCity eq 'Oslo'&$select=Id,CustomerName&$top=1&$count=true"));
        Assert.Equal(2, result.Count);
        var row = Assert.Single(result.Shaped!);
        Assert.Equal(2, row.Count);
        Assert.True(row.ContainsKey("CustomerName"));
    }

    [Fact]
    public void Untrusted_input_is_rejected()
    {
        Assert.Throws<ODataException>(() => Ids("$filter=Secret eq 1"));                       // unknown member
        Assert.Throws<ODataException>(() => Ids("$filter=System.IO.File.Delete('x') eq 1"));  // not a supported function
        Assert.Throws<ODataException>(() => Ids("$filter=Total gt"));
        Assert.Throws<ODataException>(() => Ids("$filter=Id eq 'abc'"));
        Assert.Throws<ODataException>(() => Ids("$top=-1"));
        Assert.Throws<ODataException>(() => Ids("$filter=" + new string('(', 100) + "Id eq 1" + new string(')', 100)));
        var capped = Data().Get<OdOrder, OdOrderDto>(Mapper, ODataQueryOptions.Parse("$top=99999"), new ODataSettings { MaxTop = 2 });
        Assert.Equal(2, capped.Items.Count);
    }

    [Fact]
    public void Expand_opts_in_to_explicit_expansion_members()
    {
        var mapper = new MapperConfiguration(c =>
            c.CreateMap<OdOrder, OdOrderDto>().ForMember(d => d.CustomerName, o => { o.MapFrom(s => s.Customer.Name); o.ExplicitExpansion(); })).CreateMapper();
        var plain = Data().Get<OdOrder, OdOrderDto>(mapper, ODataQueryOptions.Parse("$top=1")).Items[0];
        var expanded = Data().Get<OdOrder, OdOrderDto>(mapper, ODataQueryOptions.Parse("$top=1&$expand=CustomerName")).Items[0];
        Assert.Equal("", plain.CustomerName);
        Assert.Equal("Ann", expanded.CustomerName);
    }

    private static readonly IMapper BasketMapper = new MapperConfiguration(c =>
    {
        c.CreateMap<OdBasket, OdBasketDto>();
        c.CreateMap<OdLine, OdLineDto>();
    }).CreateMapper();

    private static IQueryable<OdBasket> Baskets() => new[]
    {
        new OdBasket { Id = 1, Title = "Garden", Note = "spring sale", Lines = { new OdLine { Sku = "A", Price = 3 }, new OdLine { Sku = "B", Price = 12 } } },
        new OdBasket { Id = 2, Title = "Kitchen", Note = "Gift for Ann", Lines = { new OdLine { Sku = "C", Price = 50 } } },
        new OdBasket { Id = 3, Title = "Empty", Note = "" },
    }.AsQueryable();

    private static int[] BasketIds(string query) =>
        Baskets().Get<OdBasket, OdBasketDto>(BasketMapper, ODataQueryOptions.Parse(query)).Items.Select(i => i.Id).ToArray();

    [Fact]
    public void Filter_any_and_all_lambdas()
    {
        Assert.Equal(new[] { 1 }, BasketIds("$filter=Lines/any(l: l/Price lt 5)"));
        Assert.Equal(new[] { 1, 2 }, BasketIds("$filter=Lines/any(l: l/Price gt 10)"));
        Assert.Equal(new[] { 2, 3 }, BasketIds("$filter=Lines/all(l: l/Price gt 10)"));
        Assert.Equal(new[] { 1, 2 }, BasketIds("$filter=Lines/any()"));
        Assert.Equal(new[] { 1 }, BasketIds("$filter=Lines/any(l: l/Sku eq 'A') and Id eq 1"));
        Assert.Throws<ODataException>(() => BasketIds("$filter=Title/any(t: t eq 'x')"));
    }

    [Fact]
    public void Search_matches_string_properties_with_and_or_not_and_phrases()
    {
        Assert.Equal(new[] { 1 }, BasketIds("$search=garden"));
        Assert.Equal(new[] { 1, 2 }, BasketIds("$search=garden OR kitchen"));
        Assert.Equal(new[] { 2 }, BasketIds("$search=\"gift for\""));
        Assert.Equal(new[] { 1 }, BasketIds("$search=sale NOT kitchen"));
        Assert.Equal(new[] { 1 }, BasketIds("$search=spring garden"));
        Assert.Empty(BasketIds("$search=spring kitchen"));
    }
}
