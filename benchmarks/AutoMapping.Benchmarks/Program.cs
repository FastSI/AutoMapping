using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using AutoMapping;

BenchmarkSwitcher.FromTypes(new[] { typeof(MappingBenchmarks), typeof(ShapeBenchmarks) }).Run(args);

public class Address { public string City { get; set; } = ""; public string Street { get; set; } = ""; }
public class Line { public int Id { get; set; } public decimal Price { get; set; } public string Sku { get; set; } = ""; }

public class Order
{
    public int Id { get; set; }
    public string Customer { get; set; } = "";
    public DateTime Created { get; set; }
    public Address Address { get; set; } = new();
    public List<Line> Lines { get; set; } = new();
}

public class LineDto { public int Id { get; set; } public decimal Price { get; set; } public string Sku { get; set; } = ""; }

public class OrderDto
{
    public int Id { get; set; }
    public string Customer { get; set; } = "";
    public DateTime Created { get; set; }
    public string AddressCity { get; set; } = "";
    public string AddressStreet { get; set; } = "";
    public List<LineDto> Lines { get; set; } = new();
}

[MemoryDiagnoser]
public class MappingBenchmarks
{
    private Order _order = null!;
    private List<Order> _orders = null!;
    private AutoMapping.IMapper _AutoMapping = null!;
    private AutoMapper.IMapper _autoMapper = null!;

    [GlobalSetup]
    public void Setup()
    {
        _order = new Order
        {
            Id = 1,
            Customer = "Ada",
            Created = DateTime.UtcNow,
            Address = new Address { City = "London", Street = "Baker St" },
            Lines = Enumerable.Range(0, 10).Select(i => new Line { Id = i, Price = i * 1.5m, Sku = "SKU" + i }).ToList()
        };
        _orders = Enumerable.Repeat(_order, 1000).ToList();

        var sharp = new AutoMapping.MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Line, LineDto>();
            cfg.CreateMap<Order, OrderDto>();
        });
        sharp.CompileMappings();
        _AutoMapping = sharp.CreateMapper();

        var auto = new AutoMapper.MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Line, LineDto>();
            cfg.CreateMap<Order, OrderDto>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        _autoMapper = auto.CreateMapper();

        // warm up so first-call compile cost is excluded
        _AutoMapping.Map<OrderDto>(_order);
        _autoMapper.Map<OrderDto>(_order);
    }

    [Benchmark(Baseline = true)]
    public OrderDto HandWritten() => ToDto(_order);

    [Benchmark]
    public OrderDto AutoMapping_Single() => _AutoMapping.Map<Order, OrderDto>(_order);

    [Benchmark]
    public OrderDto AutoMapper_Single() => _autoMapper.Map<OrderDto>(_order);

    [Benchmark]
    public List<OrderDto> HandWritten_1000() => _orders.Select(ToDto).ToList();

    [Benchmark]
    public List<OrderDto> AutoMapping_1000() => _AutoMapping.Map<List<Order>, List<OrderDto>>(_orders);

    [Benchmark]
    public List<OrderDto> AutoMapper_1000() => _autoMapper.Map<List<OrderDto>>(_orders);

    private static OrderDto ToDto(Order o) => new()
    {
        Id = o.Id,
        Customer = o.Customer,
        Created = o.Created,
        AddressCity = o.Address?.City ?? "",
        AddressStreet = o.Address?.Street ?? "",
        Lines = o.Lines.Select(l => new LineDto { Id = l.Id, Price = l.Price, Sku = l.Sku }).ToList()
    };
}

public class Team { public string Name { get; set; } = ""; public Line[] Items { get; set; } = Array.Empty<Line>(); public Dictionary<string, Line> ByKey { get; set; } = new(); }
public class TeamDto { public string Name { get; set; } = ""; public LineDto[] Items { get; set; } = Array.Empty<LineDto>(); public Dictionary<string, LineDto> ByKey { get; set; } = new(); }

[MemoryDiagnoser]
public class ShapeBenchmarks
{
    private Team _team = null!;
    private AutoMapping.IMapper _AutoMapping = null!;
    private AutoMapper.IMapper _autoMapper = null!;

    [GlobalSetup]
    public void Setup()
    {
        var lines = Enumerable.Range(0, 10).Select(i => new Line { Id = i, Price = i * 1.5m, Sku = "SKU" + i }).ToArray();
        _team = new Team { Name = "A", Items = lines, ByKey = lines.ToDictionary(l => l.Sku) };

        var sharp = new AutoMapping.MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Line, LineDto>();
            cfg.CreateMap<Team, TeamDto>();
        });
        sharp.CompileMappings();
        _AutoMapping = sharp.CreateMapper();

        _autoMapper = new AutoMapper.MapperConfiguration(cfg =>
        {
            cfg.CreateMap<Line, LineDto>();
            cfg.CreateMap<Team, TeamDto>();
        }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).CreateMapper();

        _AutoMapping.Map<Team, TeamDto>(_team);
        _autoMapper.Map<TeamDto>(_team);
    }

    [Benchmark(Baseline = true)]
    public TeamDto AutoMapper_ArrayAndDictionary() => _autoMapper.Map<TeamDto>(_team);

    [Benchmark]
    public TeamDto AutoMapping_ArrayAndDictionary() => _AutoMapping.Map<Team, TeamDto>(_team);
}
