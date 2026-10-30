using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using AutoMapping;

// Same namespace as the other AddXxx extensions, so services.AddAutoMapping(...) needs no extra using.
namespace Microsoft.Extensions.DependencyInjection;

public static class ServiceCollectionExtensions
{
    private static readonly Type[] ExtensionPoints =
    {
        typeof(IValueResolver<,,>), typeof(IMemberValueResolver<,,,>), typeof(IValueConverter<,>),
        typeof(ITypeConverter<,>), typeof(IMappingAction<,>)
    };

    /// <summary>
    /// Registers <see cref="MapperConfiguration"/> (singleton) and <see cref="IMapper"/> (transient by default).
    /// Profiles, value resolvers, converters, type converters and mapping actions found in <paramref name="assemblies"/> are
    /// registered too, and are created through the container (so they can take constructor dependencies).
    /// Use <see cref="ServiceLifetime.Scoped"/> when resolvers depend on scoped services such as a DbContext.
    /// </summary>
    public static IServiceCollection AddAutoMapping(
        this IServiceCollection services,
        Action<IServiceProvider, IMapperConfigurationExpression>? configure,
        IEnumerable<Assembly> assemblies,
        ServiceLifetime lifetime = ServiceLifetime.Transient)
    {
        ArgumentNullException.ThrowIfNull(services);
        var assemblyList = assemblies?.Distinct().ToArray() ?? Array.Empty<Assembly>();

        RegisterExtensionPoints(services, assemblyList, lifetime);

        services.TryAddSingleton(sp => new MapperConfiguration(cfg =>
        {
            cfg.ConstructServicesUsing(type => ActivatorUtilities.GetServiceOrCreateInstance(sp, type));
            if (assemblyList.Length > 0) cfg.AddMaps(assemblyList);
            configure?.Invoke(sp, cfg);
        }));
        services.TryAddSingleton<IConfigurationProvider>(sp => sp.GetRequiredService<MapperConfiguration>());   // what ProjectTo(config) takes
        services.TryAdd(new ServiceDescriptor(typeof(IMapper),
            sp => new Mapper(sp.GetRequiredService<MapperConfiguration>(), type => ActivatorUtilities.GetServiceOrCreateInstance(sp, type)),
            lifetime));
        return services;
    }

    public static IServiceCollection AddAutoMapping(this IServiceCollection services, Action<IMapperConfigurationExpression> configure,
        params Assembly[] assemblies) =>
        services.AddAutoMapping((_, cfg) => configure?.Invoke(cfg), assemblies, ServiceLifetime.Transient);

    public static IServiceCollection AddAutoMapping(this IServiceCollection services, params Assembly[] assemblies) =>
        services.AddAutoMapping((Action<IServiceProvider, IMapperConfigurationExpression>?)null, assemblies, ServiceLifetime.Transient);

    public static IServiceCollection AddAutoMapping(this IServiceCollection services, params Type[] typesFromAssemblies) =>
        services.AddAutoMapping((Action<IServiceProvider, IMapperConfigurationExpression>?)null,
            typesFromAssemblies.Select(t => t.Assembly), ServiceLifetime.Transient);

    private static void RegisterExtensionPoints(IServiceCollection services, Assembly[] assemblies, ServiceLifetime lifetime)
    {
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || type.IsGenericTypeDefinition) continue;
                var isExtension = type.GetInterfaces().Any(i => i.IsGenericType && Array.IndexOf(ExtensionPoints, i.GetGenericTypeDefinition()) >= 0);
                if (isExtension) services.TryAdd(new ServiceDescriptor(type, type, lifetime));
            }
        }
    }
}
