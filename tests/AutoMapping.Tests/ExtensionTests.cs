using System.Linq.Expressions;
using AutoMapping;
using Xunit;

namespace AutoMapping.Tests;

public class ExAddress { public string City { get; set; } = ""; }
public class ExCustomer { public string Name { get; set; } = ""; public ExAddress? Address { get; set; } }
public class ExOrder { public int Id { get; set; } public decimal Total { get; set; } public ExCustomer Customer { get; set; } = new(); }
public class ExOrderDto { public int Id { get; set; } public decimal Total { get; set; } public string CustomerName { get; set; } = ""; public string CustomerAddressCity { get; set; } = ""; }

public class ExItem { public int TenantId { get; set; } public int Code { get; set; } public string Text { get; set; } = ""; }
public class ExItemDto { public int tenantid { get; set; } public int CODE { get; set; } public string Text { get; set; } = ""; }

// minimal stand-ins shaped like EF Core's model API
public class FakeProp { public string Name { get; set; } = ""; }
public class FakeKey { public IEnumerable<FakeProp> Properties { get; set; } = new List<FakeProp>(); }
public class FakeEntityType { public Type ClrType { get; set; } = null!; public FakeKey? FindPrimaryKey() => Key; public FakeKey? Key { get; set; } }
public class FakeModel { public List<FakeEntityType> Types { get; } = new(); public IEnumerable<FakeEntityType> GetEntityTypes() => Types; }
public class FakeDbContext { public FakeModel Model { get; } = new(); }

public class ExtensionTests
{
    [Fact]
    public void MapExpression_rewrites_dto_predicate_to_entity_predicate()
    {
        var cfg = new MapperConfiguration(c =>
        {
            c.CreateMap<ExOrder, ExOrderDto>();
        });
        var mapper = cfg.CreateMapper();
        Expression<Func<ExOrderDto, bool>> onDto = d => d.CustomerName == "Ann" && d.Total > 10 && d.CustomerAddressCity == "Oslo";

        var onEntity = mapper.MapExpression<ExOrder, ExOrderDto, bool>(onDto);

        var orders = new[]
        {
            new ExOrder { Id = 1, Total = 20, Customer = new ExCustomer { Name = "Ann", Address = new ExAddress { City = "Oslo" } } },
            new ExOrder { Id = 2, Total = 5,  Customer = new ExCustomer { Name = "Ann", Address = new ExAddress { City = "Oslo" } } },
            new ExOrder { Id = 3, Total = 50, Customer = new ExCustomer { Name = "Bob", Address = new ExAddress { City = "Oslo" } } },
        };
        Assert.Equal(new[] { 1 }, orders.AsQueryable().Where(onEntity).Select(o => o.Id));
        Assert.DoesNotContain("CustomerName", onEntity.ToString());
    }

    [Fact]
    public void MapExpression_works_for_selectors()
    {
        var mapper = new MapperConfiguration(c => c.CreateMap<ExOrder, ExOrderDto>()).CreateMapper();
        var key = mapper.MapExpression<ExOrder, ExOrderDto, string>(d => d.CustomerName);
        Assert.Equal("Ann", key.Compile()(new ExOrder { Customer = new ExCustomer { Name = "Ann" } }));
    }

    [Fact]
    public void EqualityComparisonByKeys_matches_on_composite_keys()
    {
        var cfg = new MapperConfiguration(c => c.CreateMap<ExItemDto, ExItem>().EqualityComparisonByKeys("TenantId", "Code"));
        var set = new List<ExItem> { new() { TenantId = 1, Code = 1, Text = "a" }, new() { TenantId = 1, Code = 2, Text = "b" } };
        var dest = new List<ExItem>(set);

        var result = cfg.CreateMapper().Map(new List<ExItemDto> { new() { tenantid = 1, CODE = 2, Text = "B!" } }, dest);

        Assert.Single(result);
        Assert.Same(set[1], result[0]);
        Assert.Equal("B!", result[0].Text);
    }

    [Fact]
    public void UseEntityFrameworkCoreModel_derives_equality_from_primary_keys()
    {
        var db = new FakeDbContext();
        db.Model.Types.Add(new FakeEntityType
        {
            ClrType = typeof(ExItem),
            Key = new FakeKey { Properties = new[] { new FakeProp { Name = "TenantId" }, new FakeProp { Name = "Code" } } }
        });
        var cfg = new MapperConfiguration(c =>
        {
            c.UseEntityFrameworkCoreModel(db);
            c.CreateMap<ExItemDto, ExItem>();
        });
        var existing = new List<ExItem> { new() { TenantId = 1, Code = 2, Text = "b" } };
        var result = cfg.CreateMapper().Map(new List<ExItemDto> { new() { tenantid = 1, CODE = 2, Text = "new" }, new() { tenantid = 9, CODE = 9, Text = "added" } }, existing);

        Assert.Equal(2, result.Count);
        Assert.Equal("new", result[0].Text);
        Assert.Equal(2, existing.Count);
    }
}
