using Xunit;

namespace AutoMapping.Tests;

public class ParityTests
{
    private static MapperConfiguration Config(Action<IMapperConfigurationExpression> cfg) => new(cfg);

    private static IMapper Create(Action<IMapperConfigurationExpression> cfg, Func<Type, object>? services = null) =>
        services == null ? Config(cfg).CreateMapper() : Config(cfg).CreateMapper(services);

    private static object Services(Type type) =>
        type == typeof(LabelResolver) ? new LabelResolver(new Prefixer()) : Activator.CreateInstance(type)!;

    // ------------------------------------------------------------------ resolvers, converters, actions, context

    [Fact]
    public void Value_resolver_member_resolver_and_value_converter()
    {
        var mapper = Create(cfg => cfg.CreateMap<Stock, StockDto>()
            .ForMember(d => d.Label, o => o.MapFrom<LabelResolver>())
            .ForMember(d => d.Total, o => o.MapFrom<TotalResolver, int>(s => s.Qty))
            .ForMember(d => d.Upper, o => o.ConvertUsing<UpperConverter, string>(s => s.Name)), Services);

        var dto = mapper.Map<StockDto>(new Stock { Name = "bolt", Qty = 5, Price = 2.5m });

        Assert.Equal("item:bolt", dto.Label);
        Assert.Equal(12.5m, dto.Total);
        Assert.Equal("BOLT", dto.Upper);
        Assert.Equal("bolt", dto.Name);
    }

    [Fact]
    public void Value_converter_finds_its_source_member_by_convention()
    {
        var mapper = Create(cfg => cfg.CreateMap<Stock, StockDto>().ForMember(d => d.Name, o => o.ConvertUsing<UpperConverter, string>()));
        Assert.Equal("BOLT", mapper.Map<StockDto>(new Stock { Name = "bolt" }).Name);
    }

    [Fact]
    public void Type_converter_and_mapping_action_with_items()
    {
        var converting = Create(cfg => cfg.CreateMap<Stock, StockDto>().ConvertUsing<StockConverter>());
        Assert.Equal("conv:x", converting.Map<StockDto>(new Stock { Name = "x" }).Label);

        var acting = Create(cfg => cfg.CreateMap<Stock, StockDto>().AfterMap<StampAction>());
        Assert.Equal("T:x", acting.Map<StockDto>(new Stock { Name = "x" }, o => o.Items["tag"] = "T").Name);
        Assert.Equal("none:x", acting.Map<StockDto>(new Stock { Name = "x" }).Name);
    }

    [Fact]
    public void Resolver_delegates_see_items_and_a_context_bound_mapper()
    {
        var mapper = Create(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Holder, HolderDto>()
                .ForMember(d => d.FirstOrder, o => o.MapFrom((s, d, m, c) => c.Mapper.Map<OrderDto>(s.FirstOrder)))
                .ForMember(d => d.Name, o => o.MapFrom((s, d, m, c) => s.Name + c.Items["suffix"]));
        });

        var dto = mapper.Map<HolderDto>(new Holder { FirstOrder = new Order { Id = 4 }, Name = "h" }, o => o.Items["suffix"] = "!");

        Assert.Equal(4, dto.FirstOrder!.Id);
        Assert.Equal("h!", dto.Name);
    }

    [Fact]
    public void Operation_level_BeforeMap_and_AfterMap()
    {
        var mapper = Create(cfg => cfg.CreateMap<Stock, StockDto>());
        var dto = mapper.Map<StockDto>(new Stock { Name = "n" }, o =>
        {
            o.BeforeMap((s, d) => d.Label = "before");
            o.AfterMap((s, d) => d.Upper = "after");
        });

        Assert.Equal("before", dto.Label);
        Assert.Equal("after", dto.Upper);
    }

    [Fact]
    public void ConstructUsingServiceLocator_creates_the_destination_through_the_service_constructor()
    {
        var mapper = Create(
            cfg => cfg.CreateMap<Stock, StockDto>().ConstructUsingServiceLocator(),
            t => t == typeof(StockDto) ? new StockDto { Label = "from-di" } : Activator.CreateInstance(t)!);

        Assert.Equal("from-di", mapper.Map<StockDto>(new Stock { Name = "n" }).Label);
    }

    // ------------------------------------------------------------------ member surface

    [Fact]
    public void ForAllMembers_condition_skips_null_sources()
    {
        var mapper = Create(cfg => cfg.CreateMap<Person, PersonDto>().ForAllMembers(o => o.Condition((s, d, srcMember) => srcMember != null)));
        var dest = new PersonDto { Notes = "keep", Last = "old" };

        mapper.Map(new Person { First = "a", Last = null, Notes = null, Age = 3 }, dest);

        Assert.Equal("a", dest.First);
        Assert.Equal(3, dest.Age);
        Assert.Equal("keep", dest.Notes);
        Assert.Equal("old", dest.Last);
    }

    [Fact]
    public void ForAllOtherMembers_ignores_everything_not_configured()
    {
        var mapper = Create(cfg => cfg.CreateMap<Person, PersonDto>()
            .ForMember(d => d.First, o => o.MapFrom(s => s.First + "!"))
            .ForAllOtherMembers(o => o.Ignore()));

        var dto = mapper.Map<PersonDto>(new Person { First = "x", Age = 9 });

        Assert.Equal("x!", dto.First);
        Assert.Equal(0, dto.Age);
    }

    [Fact]
    public void ForCtorParam_feeds_constructor_parameters()
    {
        var mapper = Create(cfg => cfg.CreateMap<Price, Money>()
            .ForCtorParam("amount", o => o.MapFrom(s => s.Value))
            .ForCtorParam("currency", o => o.MapFrom(s => "USD")));

        Assert.Equal(new Money(5m, "USD"), mapper.Map<Money>(new Price { Value = 5m }));
    }

    [Fact]
    public void ForPath_creates_intermediate_objects()
    {
        var mapper = Create(cfg => cfg.CreateMap<Flat, Nested>()
            .ForPath(d => d.Address!.City, o => o.MapFrom(s => s.AddressCity))
            .ForPath(d => d.Address!.Street, o => o.MapFrom(s => s.AddressStreet)));

        var nested = mapper.Map<Nested>(new Flat { Name = "n", AddressCity = "c", AddressStreet = "s" });

        Assert.Equal("n", nested.Name);
        Assert.Equal("c", nested.Address!.City);
        Assert.Equal("s", nested.Address.Street);
    }

    [Fact]
    public void ReverseMap_unflattens_conventional_flattening()
    {
        var mapper = Create(cfg => cfg.CreateMap<Nested, Flat>().ReverseMap());

        var back = mapper.Map<Nested>(new Flat { Name = "n", AddressCity = "c", AddressStreet = "s" });

        Assert.Equal("n", back.Name);
        Assert.Equal("c", back.Address!.City);
        Assert.Equal("s", back.Address.Street);
    }

    [Fact]
    public void ReverseMap_reverses_a_nested_MapFrom_chain()
    {
        var mapper = Create(cfg => cfg.CreateMap<Person, PersonDto>()
            .ForMember(d => d.Town, o => o.MapFrom(s => s.Home!.City))
            .ReverseMap());

        var person = mapper.Map<Person>(new PersonDto { First = "f", Town = "Paris" });

        Assert.Equal("Paris", person.Home!.City);
    }

    [Fact]
    public void IncludeMembers_looks_into_nested_source_members()
    {
        var mapper = Create(cfg => cfg.CreateMap<Outer, OuterDto>().IncludeMembers(s => s.Inner!));

        Assert.Equal("T", mapper.Map<OuterDto>(new Outer { Id = 1, Inner = new Inner { Title = "T" } }).Title);
        Assert.Null(mapper.Map<OuterDto>(new Outer { Id = 2 }).Title);
    }

    [Fact]
    public void MemberList_Source_and_None_validation()
    {
        var strict = Config(c => c.CreateMap<WideSource, NarrowDest>(MemberList.Source));
        var error = Assert.Throws<ConfigurationValidationException>(() => strict.AssertConfigurationIsValid());
        Assert.Contains(error.Errors, e => e.Contains("'Unused'"));

        Config(c => c.CreateMap<WideSource, NarrowDest>(MemberList.Source)
            .ForSourceMember(s => s.Unused, o => o.DoNotValidate())).AssertConfigurationIsValid();

        Config(c => c.CreateMap<FlagSource, FlagDest>(MemberList.None)).AssertConfigurationIsValid();
    }

    [Fact]
    public void Get_only_collections_and_dictionaries_are_filled_in_place()
    {
        var mapper = Create(cfg => cfg.CreateMap<ListSrc, ListDst>());
        var destination = new ListDst();
        var numbers = destination.Numbers;
        var lookup = destination.Lookup;

        mapper.Map(new ListSrc { Numbers = { 1, 2 }, Lookup = { ["a"] = 1 } }, destination);

        Assert.Same(numbers, destination.Numbers);
        Assert.Same(lookup, destination.Lookup);
        Assert.Equal(new[] { 1, 2 }, destination.Numbers);
        Assert.Single(destination.Lookup);
        Assert.Equal(1, destination.Lookup["a"]);

        var created = mapper.Map<ListDst>(new ListSrc { Numbers = { 7 } });
        Assert.Equal(new[] { 7 }, created.Numbers);
    }

    [Fact]
    public void UseDestinationValue_reuses_the_existing_collection()
    {
        var mapper = Create(cfg => cfg.CreateMap<ReuseSrc, ReuseDst>().ForMember(d => d.Numbers, o => o.UseDestinationValue()));
        var destination = new ReuseDst();
        var list = destination.Numbers;

        mapper.Map(new ReuseSrc { Numbers = { 5 } }, destination);

        Assert.Same(list, destination.Numbers);
        Assert.Equal(new[] { 5 }, destination.Numbers);
    }

    [Fact]
    public void Null_handling_options()
    {
        var allowNull = Create(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Customer, CustomerDto>()
                .ForMember(d => d.FullName, o => o.Ignore())
                .ForMember(d => d.Orders, o => o.AllowNull());
        });
        Assert.Null(allowNull.Map<CustomerDto>(new Customer { FirstName = "a", Orders = null! }).Orders);

        var doNotAllow = Create(cfg => cfg.CreateMap<Person, PersonDto>().ForMember(d => d.Notes, o => o.DoNotAllowNull()));
        Assert.Equal("", doNotAllow.Map<PersonDto>(new Person { Notes = null }).Notes);

        var global = Create(cfg =>
        {
            cfg.AllowNullDestinationValues = false;
            cfg.CreateMap<Person, PersonDto>();
        });
        var dto = global.Map<PersonDto>(new Person());
        Assert.Equal("", dto.Last);
        Assert.Equal("", dto.HomeCity);
    }

    [Fact]
    public void PreCondition_skips_resolving_the_member()
    {
        var mapper = Create(cfg => cfg.CreateMap<FlagSource, FlagDest>()
            .ForMember(d => d.Count, o => o.PreCondition(s => s.Count > 0))
            .ForMember(d => d.Extra, o => o.Ignore()));

        Assert.Equal(0, mapper.Map<FlagDest>(new FlagSource { Name = "a", Count = 0 }).Count);
        Assert.Equal(4, mapper.Map<FlagDest>(new FlagSource { Name = "a", Count = 4 }).Count);
    }

    [Fact]
    public void MapAtRuntime_uses_the_runtime_type_of_the_source_value()
    {
        var mapper = Create(cfg =>
        {
            cfg.CreateMap<Animal, AnimalDto>().Include<Dog, DogDto>();
            cfg.CreateMap<Dog, DogDto>();
            cfg.CreateMap<Zoo, ZooDto>().ForMember(d => d.Pet, o => o.MapAtRuntime());
        });

        var zoo = mapper.Map<ZooDto>(new Zoo { Pet = new Dog { Name = "Rex", Tricks = 2 } });

        Assert.Equal(2, Assert.IsType<DogDto>(zoo.Pet).Tricks);
    }

    [Fact]
    public void SetMappingOrder_controls_member_order()
    {
        var log = new List<string>();
        var mapper = Create(cfg => cfg.CreateMap<Ordered, Ordered>()
            .ForMember(d => d.A, o => { o.SetMappingOrder(3); o.MapFrom((s, d) => { log.Add("A"); return s.A; }); })
            .ForMember(d => d.B, o => { o.SetMappingOrder(1); o.MapFrom((s, d) => { log.Add("B"); return s.B; }); })
            .ForMember(d => d.C, o => { o.SetMappingOrder(2); o.MapFrom((s, d) => { log.Add("C"); return s.C; }); }));

        mapper.Map<Ordered>(new Ordered());

        Assert.Equal(new[] { "B", "C", "A" }, log);
    }

    // ------------------------------------------------------------------ graph / inheritance

    [Fact]
    public void MaxDepth_stops_at_the_given_depth()
    {
        var n4 = new Node { Name = "4" };
        var n3 = new Node { Name = "3", Next = n4 };
        var n2 = new Node { Name = "2", Next = n3 };
        var n1 = new Node { Name = "1", Next = n2 };

        var dto = Create(cfg => cfg.CreateMap<Node, NodeDto>().MaxDepth(2)).Map<NodeDto>(n1);

        Assert.NotNull(dto.Next);
        Assert.Null(dto.Next!.Next);
    }

    [Fact]
    public void IncludeAllDerived_As_and_interface_source_maps()
    {
        var derived = Create(cfg =>
        {
            cfg.CreateMap<Animal, AnimalDto>().IncludeAllDerived();
            cfg.CreateMap<Dog, DogDto>();
        });
        Animal animal = new Dog { Name = "r", Tricks = 1 };
        Assert.IsType<DogDto>(derived.Map<Animal, AnimalDto>(animal));

        var implementation = Create(cfg =>
        {
            cfg.CreateMap<ThingSrc, IThing>().As<Thing>();
            cfg.CreateMap<ThingSrc, Thing>();
        });
        var thing = implementation.Map<IThing>(new ThingSrc { Name = "x" });
        Assert.IsType<Thing>(thing);
        Assert.Equal("x", thing.Name);

        var viaInterface = Create(cfg => cfg.CreateMap<IThing, ThingDto>());
        Assert.Equal("q", viaInterface.Map<ThingDto>(new Thing { Name = "q" }).Name);
    }

    [Fact]
    public void Open_generic_maps_and_open_generic_converters()
    {
        var page = new Page<Order> { Total = 7, Items = { new Order { Id = 1 }, new Order { Id = 2 } } };

        var plain = Create(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap(typeof(Page<>), typeof(PageDto<>));
        });
        var dto = plain.Map<PageDto<OrderDto>>(page);
        Assert.Equal(7, dto.Total);
        Assert.Equal(2, dto.Items.Count);

        var converted = Create(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap(typeof(Page<>), typeof(PageDto<>)).ConvertUsing(typeof(PageConverter<,>));
        });
        Assert.Equal(107, converted.Map<PageDto<OrderDto>>(page).Total);
    }

    // ------------------------------------------------------------------ conventions and settings

    [Fact]
    public void Prefixes_postfixes_replacements_and_global_ignores()
    {
        var mapper = Create(cfg =>
        {
            cfg.RecognizePrefixes("Src");
            cfg.RecognizePostfixes("Value");
            cfg.ReplaceMemberName("Ä", "A");
            cfg.AddGlobalIgnore("Internal");
            cfg.CreateMap<PrefixedSource, PrefixedDest>();
        });

        var dest = mapper.Map<PrefixedDest>(new PrefixedSource { SrcName = "n", TitleValue = "t", Äge = "7" });

        Assert.Equal("n", dest.Name);
        Assert.Equal("t", dest.Title);
        Assert.Equal("7", dest.Age);
        Assert.Equal("untouched", dest.InternalNote);
    }

    [Fact]
    public void Destination_prefixes_ShouldMapProperty_and_naming_convention()
    {
        var withPrefix = Create(cfg =>
        {
            cfg.RecognizeDestinationPrefixes("Dest");
            cfg.CreateMap<DestPrefixSource, DestPrefixDest>();
        });
        Assert.Equal("t", withPrefix.Map<DestPrefixDest>(new DestPrefixSource { Title = "t" }).DestTitle);

        var hidden = Create(cfg =>
        {
            cfg.ShouldMapProperty = p => p.Name != "Secret";
            cfg.CreateMap<SecretSource, SecretDest>();
        });
        var secret = hidden.Map<SecretDest>(new SecretSource { Open = "o", Secret = "shh" });
        Assert.Equal("o", secret.Open);
        Assert.Equal("kept", secret.Secret);

        var snake = Create(cfg =>
        {
            cfg.DestinationMemberNamingConvention = new LowerUnderscoreNamingConvention();
            cfg.CreateMap<AddressHolder, SnakeDest>();
        });
        Assert.Equal("c", snake.Map<SnakeDest>(new AddressHolder { Address = new Address { City = "c" } }).address_city);
    }

    [Fact]
    public void Value_transformers_run_global_then_per_map_and_skip_null()
    {
        var mapper = Create(cfg =>
        {
            cfg.ValueTransformers.Add<string>(s => s.Trim());
            cfg.CreateMap<TextSrc, TextDst>().AddTransform<string>(s => s + "!");
        });

        var result = mapper.Map<TextDst>(new TextSrc { A = " a ", B = null });

        Assert.Equal("a!", result.A);
        Assert.Null(result.B);
    }

    [Fact]
    public void Profile_settings_override_the_root_and_profiles_validate_separately()
    {
        var config = Config(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.AddProfile<StrictProfile>();
        });

        Assert.Null(config.CreateMapper().Map<CustomerDto>(new Customer { Orders = null! }).Orders);
        config.AssertConfigurationIsValid<StrictProfile>();
        config.AssertConfigurationIsValid();
    }

    [Fact]
    public void Execution_plan_and_mapping_plan_are_available()
    {
        var config = Config(c => c.CreateMap<Order, OrderDto>());

        var plan = config.BuildExecutionPlan(typeof(Order), typeof(OrderDto));

        Assert.Equal(3, plan.Parameters.Count);
        Assert.Equal(typeof(OrderDto), plan.ReturnType);
        Assert.Contains("Total <- Total", config.GetMappingPlan(typeof(Order), typeof(OrderDto)));
    }

    [Fact]
    public void Runtime_type_maps_and_by_name_member_configuration()
    {
        var runtime = Create(cfg => cfg.CreateMap(typeof(Order), typeof(OrderDto)));
        Assert.Equal(3, ((OrderDto)runtime.Map(new Order { Id = 3 }, typeof(Order), typeof(OrderDto))!).Id);

        var byName = Create(cfg => cfg.CreateMap<Stock, StockDto>().ForMember("Total", o => o.MapFrom(s => s.Price)));
        Assert.Equal(2.5m, byName.Map<StockDto>(new Stock { Price = 2.5m }).Total);

        var byPath = Create(cfg => cfg.CreateMap<Person, PersonDto>().ForMember(d => d.Town, o => o.MapFrom("Home.City")));
        Assert.Equal("Rome", byPath.Map<PersonDto>(new Person { Home = new Address { City = "Rome" } }).Town);
    }

    // ------------------------------------------------------------------ ProjectTo

    [Fact]
    public void ProjectTo_explicit_expansion_and_parameters()
    {
        var config = Config(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            var currentUser = "?";
            cfg.CreateMap<Cust2, Cust2Dto>()
                .ForMember(d => d.Orders, o => o.ExplicitExpansion())
                .ForMember(d => d.Tag, o => o.MapFrom(s => s.Name + currentUser));
        });
        var query = new[] { new Cust2 { Id = 1, Name = "Ann", Orders = { new Order { Id = 9 } } } }.AsQueryable();

        var plain = query.ProjectTo<Cust2Dto>(config).Single();
        Assert.Empty(plain.Orders);
        Assert.Equal("Ann?", plain.Tag);

        var expanded = query.ProjectTo<Cust2Dto>(config, null, d => d.Orders).Single();
        Assert.Single(expanded.Orders);

        var parameterized = query.ProjectTo<Cust2Dto>(config, new { currentUser = "!" }).Single();
        Assert.Equal("Ann!", parameterized.Tag);
    }
}
