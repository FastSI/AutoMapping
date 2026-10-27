using Xunit;

namespace AutoMapping.Tests;

public class GapTests
{
    private static MapperConfiguration Config(Action<IMapperConfigurationExpression> cfg) => new(cfg);

    [Fact]
    public void SourceMemberNamingConvention_decides_how_source_names_are_matched()
    {
        var mapper = Config(cfg =>
        {
            cfg.SourceMemberNamingConvention = new MPrefixConvention();
            cfg.CreateMap<MPrefixed, MPlain>();
        }).CreateMapper();

        var plain = mapper.Map<MPlain>(new MPrefixed { m_Name = "n", m_Age = 4 });

        Assert.Equal("n", plain.Name);
        Assert.Equal(4, plain.Age);
    }

    [Fact]
    public void Nested_operation_options_apply_to_the_nested_map_only()
    {
        var log = new List<string>();
        var mapper = Config(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Holder, HolderDto>()
                .ForMember(d => d.FirstOrder, o => o.MapFrom((s, d, m, c) =>
                    c.Mapper.Map<OrderDto>(s.FirstOrder, x =>
                    {
                        x.Items["k"] = "v";
                        x.AfterMap((a, b) => { b.Id += 100; log.Add("nested-after"); });
                    })))
                .ForMember(d => d.Name, o => o.MapFrom((s, d, m, c) => s.Name + c.Items["k"]));
        }).CreateMapper();

        var dto = mapper.Map<HolderDto>(new Holder { FirstOrder = new Order { Id = 1 }, Name = "n" }, o => o.AfterMap((s, d) => log.Add("outer-after")));

        Assert.Equal(101, dto.FirstOrder!.Id);
        Assert.Equal("n" + "v", dto.Name);
        Assert.Equal(new[] { "nested-after", "outer-after" }, log);
    }

    [Fact]
    public void Nested_ConstructServicesUsing_is_scoped_to_the_nested_call()
    {
        var mapper = Config(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Stock, StockDto>().ConstructUsingServiceLocator();
            cfg.CreateMap<Holder, HolderDto>()
                .ForMember(d => d.Name, o => o.MapFrom((s, d, m, c) =>
                    c.Mapper.Map<StockDto>(new Stock(), x => x.ConstructServicesUsing(_ => new StockDto { Label = "nested" })).Label +
                    c.CreateInstance(typeof(Prefixer)).GetType().Name));
        }).CreateMapper(t => Activator.CreateInstance(t)!);

        Assert.Equal("nestedPrefixer", mapper.Map<HolderDto>(new Holder()).Name);
    }

    [Fact]
    public void MapWhen_works_at_runtime_and_in_ProjectTo()
    {
        var config = Config(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Cust2, Cust2Dto>()
                .ForMember(d => d.Orders, o => o.Ignore())
                .ForMember(d => d.Tag, o => o.MapFrom(s => s.Name).MapWhen(s => s.Id > 1));
        });
        var mapper = config.CreateMapper();

        Assert.Equal("", mapper.Map<Cust2Dto>(new Cust2 { Id = 1, Name = "a" }).Tag);
        Assert.Equal("a", mapper.Map<Cust2Dto>(new Cust2 { Id = 2, Name = "a" }).Tag);

        var rows = new[] { new Cust2 { Id = 1, Name = "a" }, new Cust2 { Id = 2, Name = "b" } }.AsQueryable();
        var projected = rows.ProjectTo<Cust2Dto>(config).ToList();
        Assert.Null(projected[0].Tag);
        Assert.Equal("b", projected[1].Tag);
    }

    [Fact]
    public void Private_internal_and_protected_setters_are_mapped_but_not_projected()
    {
        var config = Config(cfg => cfg.CreateMap<Person, PrivDest>());
        var dest = config.CreateMapper().Map<PrivDest>(new Person { First = "f", Age = 5, Notes = "n" });

        Assert.Equal("f", dest.First);
        Assert.Equal(5, dest.Age);
        Assert.Equal("n", dest.Notes);

        var projected = new[] { new Person { First = "f", Age = 5 } }.AsQueryable().ProjectTo<PrivDest>(config).Single();
        Assert.Equal("", projected.First);
        Assert.Equal(0, projected.Age);
    }

    [Fact]
    public void Dictionary_maps_to_object_and_object_to_dictionary()
    {
        var mapper = Config(cfg =>
        {
            cfg.CreateMap<BagSrc, BagDst>();
        }).CreateMapper();

        var bag = new Dictionary<string, object?> { ["first"] = "a", ["AGE"] = "7", ["unknown"] = 1, ["Last"] = null };
        var dst = mapper.Map<BagDst>(new BagSrc { Data = bag });
        Assert.Equal("a", dst.Data!.First);
        Assert.Equal(7, dst.Data.Age);
        Assert.Null(dst.Data.Last);

        var direct = mapper.Map<PersonDto>(bag);
        Assert.Equal("a", direct.First);

        var back = mapper.Map<Dictionary<string, object?>>(new PersonDto { First = "x", Age = 3 });
        Assert.Equal("x", back["First"]);
        Assert.Equal(3, back["Age"]);

        var typed = mapper.Map<Dictionary<string, string>>(new PersonDto { First = "x", Age = 3 });
        Assert.Equal("3", typed["Age"]);
    }
}
