#if !NO_DI
using Microsoft.Extensions.DependencyInjection;
#endif
using Xunit;

namespace AutoMapping.Tests;

public class MapperTests
{
    private static IMapper Build(Action<IMapperConfigurationExpression>? extra = null)
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Order, OrderDto>();
            cfg.CreateMap<Customer, CustomerDto>()
                .ForMember(d => d.FullName, o => o.MapFrom(s => s.FirstName + " " + s.LastName));
            extra?.Invoke(cfg);
        });
        return config.CreateMapper();
    }

    private static Customer SampleCustomer() => new()
    {
        Id = 7,
        FirstName = "Ada",
        LastName = "Lovelace",
        Created = new DateTime(2026, 1, 2),
        Address = new Address { City = "London" },
        Status = Status.Active,
        Age = 36,
        Score = "42",
        Orders = { new Order { Id = 1, Total = 9.5m }, new Order { Id = 2, Total = 3m } },
        OrdersByKey = { ["a"] = new Order { Id = 5, Total = 1m } }
    };

    [Fact]
    public void Maps_matching_members_custom_members_and_flattening()
    {
        var dto = Build().Map<CustomerDto>(SampleCustomer());

        Assert.Equal(7, dto.Id);
        Assert.Equal("Ada Lovelace", dto.FullName);
        Assert.Equal(new DateTime(2026, 1, 2), dto.Created);
        Assert.Equal("London", dto.AddressCity);
        Assert.Equal("ada", dto.Nickname);   // GetNickname() method convention
    }

    [Fact]
    public void Converts_types_enum_to_string_nullable_widening_and_string_to_int()
    {
        var dto = Build().Map<CustomerDto>(SampleCustomer());

        Assert.Equal("Active", dto.Status);
        Assert.Equal(36L, dto.Age);
        Assert.Equal(42, dto.Score);
    }

    [Fact]
    public void Maps_collections_and_dictionaries_with_nested_maps()
    {
        var dto = Build().Map<CustomerDto>(SampleCustomer());

        Assert.Equal(2, dto.Orders.Count);
        Assert.Equal(9.5, dto.Orders[0].Total);
        Assert.Equal(5, dto.OrdersByKey["a"].Id);
    }

    [Fact]
    public void Null_nested_source_flattens_to_default_instead_of_throwing()
    {
        var customer = SampleCustomer();
        customer.Address = null;
        Assert.Null(Build().Map<CustomerDto>(customer).AddressCity);
    }

    [Fact]
    public void Null_source_collection_becomes_empty_unless_AllowNullCollections()
    {
        var customer = SampleCustomer();
        customer.Orders = null!;
        Assert.Empty(Build().Map<CustomerDto>(customer).Orders);

        var allowNull = Build(c => c.AllowNullCollections = true);
        Assert.Null(allowNull.Map<CustomerDto>(customer).Orders);
    }

    [Fact]
    public void Top_level_collections_map()
    {
        var mapper = Build();
        var dtos = mapper.Map<List<OrderDto>>(new[] { new Order { Id = 1 }, new Order { Id = 2 } });
        var array = mapper.Map<OrderDto[]>(new List<Order> { new() { Id = 3 } });

        Assert.Equal(2, dtos.Count);
        Assert.Equal(3, array[0].Id);
    }

    [Fact]
    public void Maps_onto_existing_destination()
    {
        var existing = new OrderDto { Id = 99, Total = 1 };
        var result = Build().Map(new Order { Id = 5, Total = 2m }, existing);

        Assert.Same(existing, result);
        Assert.Equal(5, existing.Id);
        Assert.Equal(2d, existing.Total);
    }

    [Fact]
    public void Null_source_returns_default()
    {
        Assert.Null(Build().Map<OrderDto>(null));
        Assert.Null(Build().Map<Order, OrderDto>(null!));
    }

    [Fact]
    public void Ignore_condition_precondition_and_null_substitute()
    {
        var mapper = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<FlagSource, FlagDest>()
                .ForMember(d => d.Extra, o => o.Ignore())
                .ForMember(d => d.Count, o => o.Condition((s, d) => s.Count > 0))
                .ForMember(d => d.Name, o => o.NullSubstitute("n/a"));
        }).CreateMapper();

        var dest = mapper.Map<FlagDest>(new FlagSource { Name = null!, Count = 0 });
        Assert.Equal("n/a", dest.Name);
        Assert.Equal(0, dest.Count);
        Assert.Equal("", dest.Extra);

        Assert.Equal(3, mapper.Map<FlagDest>(new FlagSource { Name = "x", Count = 3 }).Count);
    }

    [Fact]
    public void BeforeMap_AfterMap_ConstructUsing_and_UseValue_run()
    {
        var calls = new List<string>();
        var mapper = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<FlagSource, FlagDest>()
                .ConstructUsing(s => new FlagDest { Extra = "ctor" })
                .BeforeMap((s, d) => calls.Add("before"))
                .AfterMap((s, d) => calls.Add("after"))
                .ForMember(d => d.Count, o => o.UseValue(100));
        }).CreateMapper();

        var dest = mapper.Map<FlagDest>(new FlagSource { Name = "n", Count = 1 });

        Assert.Equal(new[] { "before", "after" }, calls);
        Assert.Equal("ctor", dest.Extra);
        Assert.Equal(100, dest.Count);
    }

    [Fact]
    public void ConvertUsing_replaces_the_mapping()
    {
        var mapper = new MapperConfiguration(cfg =>
            cfg.CreateMap<Order, OrderDto>().ConvertUsing(o => new OrderDto { Id = -o.Id })).CreateMapper();

        Assert.Equal(-4, mapper.Map<OrderDto>(new Order { Id = 4 }).Id);
    }

    [Fact]
    public void Records_are_built_through_their_constructor()
    {
        var mapper = new MapperConfiguration(cfg => cfg.CreateMap<PersonSource, PersonRecord>()).CreateMapper();
        var record = mapper.Map<PersonRecord>(new PersonSource { Id = 3, Name = "Grace" });

        Assert.Equal(new PersonRecord(3, "Grace"), record);
    }

    [Fact]
    public void ReverseMap_reverses_simple_MapFrom()
    {
        var mapper = new MapperConfiguration(cfg =>
            cfg.CreateMap<PersonSource, FlagDest>()
                .ForMember(d => d.Extra, o => o.MapFrom(s => s.Name))
                .ReverseMap()).CreateMapper();

        var back = mapper.Map<PersonSource>(new FlagDest { Extra = "from-extra", Name = "ignored-by-reverse" });
        Assert.Equal("from-extra", back.Name);
    }

    [Fact]
    public void Include_dispatches_to_the_derived_map()
    {
        var mapper = new MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Animal, AnimalDto>().Include<Dog, DogDto>();
            cfg.CreateMap<Dog, DogDto>();
        }).CreateMapper();

        Animal animal = new Dog { Name = "Rex", Tricks = 3 };
        var dto = mapper.Map<Animal, AnimalDto>(animal);

        var dog = Assert.IsType<DogDto>(dto);
        Assert.Equal(3, dog.Tricks);
        Assert.Equal("Rex", dog.Name);
    }

    [Fact]
    public void Cyclic_graph_needs_PreserveReferences_and_then_maps()
    {
        var a = new Node { Name = "a" };
        var b = new Node { Name = "b", Next = a };
        a.Next = b;

        var plain = new MapperConfiguration(cfg => cfg.CreateMap<Node, NodeDto>()).CreateMapper();
        Assert.Throws<MappingException>(() => plain.Map<NodeDto>(a));

        var preserving = new MapperConfiguration(cfg => cfg.CreateMap<Node, NodeDto>().PreserveReferences()).CreateMapper();
        var dto = preserving.Map<NodeDto>(a);
        Assert.Same(dto, dto.Next!.Next);
    }

    [Fact]
    public void Missing_nested_map_is_reported_by_validation_and_at_runtime()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<Customer, CustomerDto>()
            .ForMember(d => d.FullName, o => o.Ignore()));

        var ex = Assert.Throws<ConfigurationValidationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Contains("Missing map"));
        Assert.Throws<MappingConfigurationException>(() => config.CreateMapper().Map<CustomerDto>(SampleCustomer()));
    }

    [Fact]
    public void CreateMissingTypeMaps_builds_nested_maps_on_demand()
    {
        var mapper = new MapperConfiguration(cfg =>
        {
            cfg.CreateMissingTypeMaps = true;
            cfg.CreateMap<Customer, CustomerDto>().ForMember(d => d.FullName, o => o.Ignore());
        }).CreateMapper();

        Assert.Equal(2, mapper.Map<CustomerDto>(SampleCustomer()).Orders.Count);
    }

    [Fact]
    public void Validation_reports_unmapped_destination_members()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<FlagSource, FlagDest>());
        var ex = Assert.Throws<ConfigurationValidationException>(() => config.AssertConfigurationIsValid());
        Assert.Contains(ex.Errors, e => e.Contains("'Extra'"));

        new MapperConfiguration(cfg => cfg.CreateMap<FlagSource, FlagDest>().IgnoreUnmapped()).AssertConfigurationIsValid();
    }

    [Fact]
    public void ForMember_on_a_missing_member_fails_fast()
    {
        Assert.Throws<ArgumentException>(() => new MapperConfiguration(cfg =>
            cfg.CreateMap<Order, OrderDto>().ForMember(d => d.Id + 1, o => o.Ignore())));
    }

    [Fact]
    public void Enum_to_enum_maps_by_name_not_by_number()
    {
        var mapper = new MapperConfiguration(cfg => cfg.CreateMap<EnumHolder, EnumHolderDto>()).CreateMapper();

        Assert.Equal(StatusDto.Disabled, mapper.Map<EnumHolderDto>(new EnumHolder { Status = Status.Disabled }).Status);
    }

#if !NO_DI
    [Fact]
    public void Profiles_and_DI_registration_work()
    {
        var services = new ServiceCollection();
        services.AddAutoMapping(typeof(MapperTests).Assembly);
        using var provider = services.BuildServiceProvider();

        var mapper = provider.GetRequiredService<IMapper>();
        Assert.Equal(5, mapper.Map<OrderDto>(new Order { Id = 5 }).Id);
        Assert.Same(provider.GetRequiredService<MapperConfiguration>(), provider.GetRequiredService<IConfigurationProvider>());
    }
#endif

    [Fact]
    public void Concurrent_first_use_is_safe()
    {
        var mapper = Build();
        var customer = SampleCustomer();
        var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        Parallel.For(0, 64, _ =>
        {
            try { Assert.Equal("Ada Lovelace", mapper.Map<CustomerDto>(customer).FullName); }
            catch (Exception e) { errors.Add(e); }
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void ProjectTo_builds_a_translatable_select()
    {
        var mapper = Build(cfg => cfg.CreateMap<Customer, CustomerSummaryDto>()
            .ForMember(d => d.FullName, o => o.MapFrom(s => s.FirstName + " " + s.LastName)));
        var query = new[] { SampleCustomer() }.AsQueryable();

        var projected = mapper.ProjectTo<CustomerSummaryDto>(query).ToList();

        Assert.Equal("Ada Lovelace", projected[0].FullName);
        Assert.Equal("London", projected[0].AddressCity);
        Assert.Equal(36L, projected[0].Age);
        Assert.Equal(2, projected[0].Orders.Count);
    }

    [Fact]
    public void Mapping_plan_describes_members()
    {
        var plan = ((MapperConfiguration)Build().ConfigurationProvider).GetMappingPlan<Customer, CustomerDto>();
        Assert.Contains("AddressCity <- Address.City", plan);
        Assert.Contains("FullName <- expression", plan);
    }

    [Fact]
    public void Ml_fuzzy_matching_finds_close_names_and_is_visible_in_the_plan()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.UseMlMemberMatching(o => o.Threshold = 0.3f);
            cfg.CreateMap<FuzzySource, FuzzyDest>();
        });

        var plan = config.GetMappingPlan<FuzzySource, FuzzyDest>();
        var dest = config.CreateMapper().Map<FuzzyDest>(new FuzzySource { CustomerName = "Ada", EmailAddress = "a@b.c", Quantity = 2 });

        Assert.Equal(2, dest.Quantity);
        Assert.Equal("Ada", dest.Customer_Name);   // exact match through underscore-insensitive normalisation
        Assert.Contains("Email <- EmailAddress", plan);
        Assert.Contains("ML.NET fuzzy match", plan);
        Assert.Equal("a@b.c", dest.Email);
    }

    [Fact]
    public void Fuzzy_matching_is_off_by_default()
    {
        var config = new MapperConfiguration(cfg => cfg.CreateMap<FuzzySource, FuzzyDest>());
        Assert.Contains("Email <- UNMAPPED", config.GetMappingPlan<FuzzySource, FuzzyDest>());
    }
}

public class TestProfile : Profile
{
    public TestProfile() => CreateMap<Order, OrderDto>();
}
