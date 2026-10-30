using System.Collections.Concurrent;
using Xunit;

namespace AutoMapping.Tests;

public class RvNode { public string Name { get; set; } = ""; public RvNode? Parent { get; set; } public List<RvNode> Children { get; set; } = new(); }
public class RvNodeDto { public string Name { get; set; } = ""; public RvNodeDto? Parent { get; set; } public List<RvNodeDto> Children { get; set; } = new(); }

public struct RvPoint { public int X { get; set; } public int Y { get; set; } }
public struct RvPointDto { public int X { get; set; } public int Y { get; set; } }
public class RvShape { public RvPoint Origin { get; set; } public RvPoint? Maybe { get; set; } }
public class RvShapeDto { public RvPointDto Origin { get; set; } public RvPointDto? Maybe { get; set; } }

public class RvBase { public int Id { get; set; } public string Kind { get; set; } = ""; }
public class RvDerived : RvBase { public string Extra { get; set; } = ""; }
public class RvBaseDto { public int Id { get; set; } public string Label { get; set; } = ""; }
public class RvDerivedDto : RvBaseDto { public string Extra { get; set; } = ""; }

public class RvNullables { public int? A { get; set; } public int B { get; set; } public ColorA? C { get; set; } public string? D { get; set; } }
public class RvNullablesDto { public int A { get; set; } public int? B { get; set; } public string? C { get; set; } public ColorB? D { get; set; } }

public class RvThrower { public string Name { get; set; } = ""; }
public class RvThrowerDto { public string Name { get; set; } = ""; }

public class RvHolder { public RvAddr Addr { get; set; } = new(); }
public class RvAddr { public string City { get; set; } = ""; public string Zip { get; set; } = ""; }
public class RvHolderDto { public RvAddrDto Addr { get; set; } = new(); }
public class RvAddrDto { public string City { get; set; } = ""; public string Zip { get; set; } = "keep"; }

public class ReviewTests
{
    private static MapperConfiguration Config(Action<IMapperConfigurationExpression> cfg) => new(cfg);

    [Fact]
    public void Cyclic_parent_child_graph_with_PreserveReferences()
    {
        var mapper = Config(cfg => cfg.CreateMap<RvNode, RvNodeDto>().PreserveReferences()).CreateMapper();
        var root = new RvNode { Name = "root" };
        var child = new RvNode { Name = "child", Parent = root };
        root.Children.Add(child);

        var dto = mapper.Map<RvNodeDto>(root);

        Assert.Same(dto, dto.Children[0].Parent);
    }

    [Fact]
    public void Structs_and_nullable_structs_map()
    {
        var mapper = Config(cfg =>
        {
            cfg.CreateMap<RvPoint, RvPointDto>();
            cfg.CreateMap<RvShape, RvShapeDto>();
        }).CreateMapper();

        var dto = mapper.Map<RvShapeDto>(new RvShape { Origin = new RvPoint { X = 1, Y = 2 }, Maybe = new RvPoint { X = 3, Y = 4 } });
        Assert.Equal(2, dto.Origin.Y);
        Assert.Equal(3, dto.Maybe!.Value.X);
        Assert.Null(mapper.Map<RvShapeDto>(new RvShape()).Maybe);
    }

    [Fact]
    public void Inheritance_inherits_member_configuration_into_derived_maps()
    {
        var mapper = Config(cfg =>
        {
            cfg.CreateMap<RvBase, RvBaseDto>().ForMember(d => d.Label, o => o.MapFrom(s => s.Kind + "!")).Include<RvDerived, RvDerivedDto>();
            cfg.CreateMap<RvDerived, RvDerivedDto>();
        }).CreateMapper();

        RvBase source = new RvDerived { Id = 1, Kind = "k", Extra = "x" };
        var dto = Assert.IsType<RvDerivedDto>(mapper.Map<RvBase, RvBaseDto>(source));
        Assert.Equal("k!", dto.Label);
        Assert.Equal("x", dto.Extra);
    }

    [Fact]
    public void Nullable_conversions_in_both_directions()
    {
        var mapper = Config(cfg => cfg.CreateMap<RvNullables, RvNullablesDto>()).CreateMapper();

        var nulls = mapper.Map<RvNullablesDto>(new RvNullables { A = null, B = 5, C = null, D = null });
        Assert.Equal(0, nulls.A);
        Assert.Equal(5, nulls.B);
        Assert.Null(nulls.C);
        Assert.Null(nulls.D);

        var values = mapper.Map<RvNullablesDto>(new RvNullables { A = 7, B = 5, C = ColorA.Green, D = "green" });
        Assert.Equal(7, values.A);
        Assert.Equal("Green", values.C);
        Assert.Equal(ColorB.Green, values.D);
    }

    [Fact]
    public void Exceptions_from_resolvers_surface_with_the_original_exception()
    {
        var mapper = Config(cfg => cfg.CreateMap<RvThrower, RvThrowerDto>()
            .ForMember(d => d.Name, o => o.MapFrom(new Func<RvThrower, RvThrowerDto, string>((s, d) => throw new InvalidOperationException("boom"))))).CreateMapper();

        var ex = Assert.Throws<InvalidOperationException>(() => mapper.Map<RvThrowerDto>(new RvThrower()));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public void Mapping_onto_an_existing_nested_object_keeps_unmapped_members()
    {
        var mapper = Config(cfg =>
        {
            cfg.CreateMap<RvAddr, RvAddrDto>().ForMember(d => d.Zip, o => o.Ignore());
            cfg.CreateMap<RvHolder, RvHolderDto>();
        }).CreateMapper();

        var dest = new RvHolderDto();
        var addr = dest.Addr;
        mapper.Map(new RvHolder { Addr = new RvAddr { City = "c" } }, dest);

        Assert.Same(addr, dest.Addr);
        Assert.Equal("c", dest.Addr.City);
        Assert.Equal("keep", dest.Addr.Zip);
    }

    [Fact]
    public void Concurrent_first_use_with_inline_maps_does_not_deadlock_or_fail()
    {
        for (var round = 0; round < 20; round++)
        {
            var mapper = Config(cfg =>
            {
                cfg.CreateMissingTypeMaps = true;
                cfg.CreateMap<RvHolder, RvHolderDto>();
                cfg.CreateMap<RvNode, RvNodeDto>().PreserveReferences();
            }).CreateMapper();

            var errors = new ConcurrentBag<Exception>();
            var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            {
                try
                {
                    mapper.Map<RvHolderDto>(new RvHolder { Addr = new RvAddr { City = "c" } });
                    mapper.Map<RvNodeDto>(new RvNode { Name = "n", Children = { new RvNode() } });
                }
                catch (Exception ex) { errors.Add(ex); }
            })).ToArray();

            Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(30)));
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void Reused_mapper_and_config_are_stateless_between_calls()
    {
        var mapper = Config(cfg => cfg.CreateMap<Stock, StockDto>().ForMember(d => d.Label, o => o.MapFrom((s, d, m, c) => (string)c.Items["x"]!))).CreateMapper();

        Assert.Equal("1", mapper.Map<StockDto>(new Stock(), o => o.Items["x"] = "1").Label);
        Assert.Equal("2", mapper.Map<StockDto>(new Stock(), o => o.Items["x"] = "2").Label);
        Assert.Throws<KeyNotFoundException>(() => mapper.Map<StockDto>(new Stock()));
    }

    [Fact]
    public void Arrays_sets_and_readonly_collections_map_from_any_enumerable()
    {
        var mapper = Config(cfg => cfg.CreateMap<Order, OrderDto>()).CreateMapper();
        var orders = new[] { new Order { Id = 1 }, new Order { Id = 2 } };

        Assert.Equal(2, mapper.Map<OrderDto[]>(orders).Length);
        Assert.Equal(2, mapper.Map<HashSet<OrderDto>>(orders.ToList()).Count);
        Assert.Equal(2, mapper.Map<IReadOnlyList<OrderDto>>(orders.Select(o => o)).Count);
        Assert.Equal(2, mapper.Map<List<OrderDto>>(new Queue<Order>(orders)).Count);
    }
}
