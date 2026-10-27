#if !NO_DI
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AutoMapping.Tests;

public class DiTests
{
    [Fact]
    public void Resolvers_are_created_through_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Prefixer>();
        services.AddAutoMapping(cfg => cfg.CreateMap<Stock, StockDto>().ForMember(d => d.Label, o => o.MapFrom<LabelResolver>()), typeof(DiTests).Assembly);
        using var provider = services.BuildServiceProvider();

        Assert.Equal("item:bolt", provider.GetRequiredService<IMapper>().Map<StockDto>(new Stock { Name = "bolt" }).Label);
    }

    [Fact]
    public void Lifetimes_and_configuration_registration()
    {
        var services = new ServiceCollection();
        services.AddAutoMapping(null, new[] { typeof(DiTests).Assembly }, ServiceLifetime.Scoped);   // Order -> OrderDto comes from the scanned OrderProfile
        using var provider = services.BuildServiceProvider(validateScopes: true);

        using var scopeA = provider.CreateScope();
        using var scopeB = provider.CreateScope();
        var inA = scopeA.ServiceProvider.GetRequiredService<IMapper>();
        Assert.Same(inA, scopeA.ServiceProvider.GetRequiredService<IMapper>());
        Assert.False(ReferenceEquals(inA, scopeB.ServiceProvider.GetRequiredService<IMapper>()));

        var config = provider.GetRequiredService<IConfigurationProvider>();
        Assert.Same(provider.GetRequiredService<MapperConfiguration>(), config);
        var rows = new[] { new Order { Id = 3 } }.AsQueryable();
        Assert.Equal(3, rows.ProjectTo<OrderDto>(config).Single().Id);
    }
}
#endif
