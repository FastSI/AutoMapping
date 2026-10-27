using Xunit;

namespace AutoMapping.Tests;

public class Gap2Tests
{
    private static MapperConfiguration Config(Action<IMapperConfigurationExpression> cfg) => new(cfg);

    [Fact]
    public void ConvertUsing_expression_works_at_runtime_and_in_ProjectTo()
    {
        var config = Config(cfg => cfg.CreateMap<Stock, StockDto>().ConvertUsing(s => new StockDto { Label = s.Name + "!", Total = s.Qty * s.Price }));
        var source = new Stock { Name = "n", Qty = 2, Price = 3m };

        Assert.Equal("n!", config.CreateMapper().Map<StockDto>(source).Label);

        var projected = new[] { source }.AsQueryable().ProjectTo<StockDto>(config).Single();
        Assert.Equal("n!", projected.Label);
        Assert.Equal(6m, projected.Total);
    }

    [Fact]
    public void RecognizeAlias_matches_a_source_member_by_another_name()
    {
        var mapper = Config(cfg =>
        {
            cfg.RecognizeAlias("Nick", "Handle");
            cfg.CreateMap<AliasSrc, AliasDst>();
        }).CreateMapper();

        Assert.Equal("zed", mapper.Map<AliasDst>(new AliasSrc { Nick = "zed" }).Handle);
    }

    [Fact]
    public void ForAllMaps_configures_every_map()
    {
        var log = new List<string>();
        var mapper = Config(cfg =>
        {
            cfg.ForAllMaps((info, map) => map.AfterMap((s, d) => log.Add(info.SourceType.Name)));
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Stock, StockDto>();
        }).CreateMapper();

        mapper.Map<OrderDto>(new Order());
        mapper.Map<StockDto>(new Stock());

        Assert.Equal(new[] { "Order", "Stock" }, log);
    }

    [Fact]
    public void ForAllMaps_can_apply_member_rules()
    {
        var mapper = Config(cfg =>
        {
            cfg.ForAllMaps((info, map) => map.ForAllMembers(o => o.Condition((s, d, member) => member != null)));
            cfg.CreateMap<Person, PersonDto>();
        }).CreateMapper();

        var dest = new PersonDto { Last = "keep" };
        mapper.Map(new Person { First = "a", Last = null }, dest);

        Assert.Equal("a", dest.First);
        Assert.Equal("keep", dest.Last);
    }

    [Fact]
    public void ShouldUseConstructor_filters_candidate_constructors()
    {
        var parameterless = Config(cfg =>
        {
            cfg.ShouldUseConstructor = c => c.GetParameters().Length == 0;
            cfg.CreateMap<CtorChoiceSrc, CtorChoice>();
        }).CreateMapper();
        Assert.Equal("default", parameterless.Map<CtorChoice>(new CtorChoiceSrc { Name = "n" }).Via);

        var any = Config(cfg => cfg.CreateMap<CtorChoiceSrc, CtorChoice>()).CreateMapper();
        Assert.Equal("param", any.Map<CtorChoice>(new CtorChoiceSrc { Name = "n" }).Via);
    }

    [Fact]
    public void CreateProjection_maps_only_through_ProjectTo()
    {
        var config = Config(cfg => cfg.CreateProjection<Order, OrderDto>());
        config.AssertConfigurationIsValid();

        var rows = new[] { new Order { Id = 4 } }.AsQueryable();
        Assert.Equal(4, rows.ProjectTo<OrderDto>(config).Single().Id);
        Assert.Throws<MappingConfigurationException>(() => config.CreateMapper().Map<OrderDto>(new Order()));
    }

    [Fact]
    public void ValidateInlineMaps_controls_validation_of_on_the_fly_maps()
    {
        void Configure(IMapperConfigurationExpression cfg)
        {
            cfg.CreateMissingTypeMaps = true;
            cfg.CreateMap<Outer2, Outer2Dto>();
        }

        var strict = Config(Configure);
        Assert.Throws<ConfigurationValidationException>(() => strict.AssertConfigurationIsValid());

        Config(cfg =>
        {
            Configure(cfg);
            cfg.ValidateInlineMaps = false;
        }).AssertConfigurationIsValid();
    }

    [Fact]
    public void EnableNullPropagationForQueryMapping_guards_projection_chains()
    {
        var rows = new[] { new Person { First = "a", Home = null } }.AsQueryable();

        var off = Config(cfg => cfg.CreateMap<Person, PersonDto>());
        Assert.Throws<NullReferenceException>(() => rows.ProjectTo<PersonDto>(off).ToList());

        var on = Config(cfg =>
        {
            cfg.EnableNullPropagationForQueryMapping = true;
            cfg.CreateMap<Person, PersonDto>();
        });
        Assert.Null(rows.ProjectTo<PersonDto>(on).Single().HomeCity);
    }

    [Fact]
    public void Collection_equivalency_updates_adds_and_removes_in_place()
    {
        var mapper = Config(cfg =>
        {
            cfg.AddCollectionMappers();
            cfg.CreateMap<ItemSrc, ItemDst>().EqualityComparison((s, d) => s.Id == d.Id);
            cfg.CreateMap<ParentSrc, ParentDst>();
        }).CreateMapper();

        var kept = new ItemDst { Id = 1, Name = "old" };
        var removed = new ItemDst { Id = 2, Name = "x" };
        var list = new List<ItemDst> { kept, removed };
        var source = new List<ItemSrc> { new() { Id = 1, Name = "new" }, new() { Id = 3, Name = "three" } };

        var result = mapper.Map(source, list);

        Assert.Same(list, result);
        Assert.Equal(2, list.Count);
        Assert.Same(kept, list.Single(i => i.Id == 1));
        Assert.Equal("new", kept.Name);
        Assert.Contains(list, i => i.Id == 3 && i.Name == "three");
        Assert.DoesNotContain(removed, list);

        var parent = new ParentDst { Items = new List<ItemDst> { kept, removed } };
        mapper.Map(new ParentSrc { Items = { new ItemSrc { Id = 1, Name = "again" } } }, parent);
        Assert.Single(parent.Items);
        Assert.Same(kept, parent.Items[0]);
        Assert.Equal("again", kept.Name);
    }

    [Fact]
    public void Enum_mapping_by_name_by_value_and_explicit_values_with_validation()
    {
        var mapper = Config(cfg =>
        {
            cfg.CreateMap<ColorA, ColorB>().ConvertUsingEnumMapping(o => o.MapValue(ColorA.Blue, ColorB.Crimson));
            cfg.CreateMap<NumA, NumB>().ConvertUsingEnumMapping(o => o.MapByValue());
        }).CreateMapper();

        Assert.Equal(ColorB.Green, mapper.Map<ColorB>(ColorA.Green));
        Assert.Equal(ColorB.Crimson, mapper.Map<ColorB>(ColorA.Blue));
        Assert.Equal(NumB.Dos, mapper.Map<NumB>(NumA.Two));

        var incomplete = Config(cfg =>
        {
            cfg.EnableEnumMappingValidation();
            cfg.CreateMap<ColorA, ColorB>().ConvertUsingEnumMapping();
        });
        var error = Assert.Throws<ConfigurationValidationException>(() => incomplete.AssertConfigurationIsValid());
        Assert.Contains(error.Errors, e => e.Contains("Blue"));
        Assert.Throws<MappingException>(() => incomplete.CreateMapper().Map<ColorB>(ColorA.Blue));
    }
}
