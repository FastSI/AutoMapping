using System.Reflection;

namespace AutoMapping;

/// <summary>Root configuration object. Also the "default profile": maps created here use the root settings.</summary>
public sealed class MapperConfigurationExpression : Profile, IMapperConfigurationExpression
{
    internal List<Profile> Profiles { get; } = new();
    internal Func<Type, object> ServiceCtor { get; private set; } = static type => Activator.CreateInstance(type)
        ?? throw new MappingConfigurationException($"Could not create an instance of {type.Name}.");

    public MapperConfigurationExpression() : base(string.Empty)
    {
        SourcePrefixes.Add("Get");
    }

    public int MaxDepth { get; set; } = 64;
    public bool EnableNullPropagationForQueryMapping { get; set; }
    public bool ValidateInlineMaps { get; set; } = true;
    internal bool EnumMappingValidation { get; private set; }

    public void EnableEnumMappingValidation() => EnumMappingValidation = true;

    public IMapperConfigurationExpression AddCollectionMappers() => this;

    internal MlMatchingOptions MlMatching { get; } = new();

    public IMapperConfigurationExpression AddProfile<TProfile>() where TProfile : Profile, new() => AddProfile(new TProfile());

    public IMapperConfigurationExpression AddProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ReferenceEquals(profile, this) && !Profiles.Contains(profile)) Profiles.Add(profile);
        return this;
    }

    public IMapperConfigurationExpression AddProfile(Type profileType)
    {
        ArgumentNullException.ThrowIfNull(profileType);
        if (!typeof(Profile).IsAssignableFrom(profileType) || profileType.IsAbstract)
            throw new MappingConfigurationException($"{profileType.Name} is not a concrete Profile.");
        return AddProfile((Profile)Activator.CreateInstance(profileType, nonPublic: true)!);
    }

    public IMapperConfigurationExpression AddProfiles(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition || !typeof(Profile).IsAssignableFrom(type)) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;
                AddProfile(type);
            }
        }
        return this;
    }

    public IMapperConfigurationExpression AddMaps(params Assembly[] assemblies) => AddProfiles(assemblies);

    public IMapperConfigurationExpression AddMaps(params Type[] typesFromAssemblies) =>
        AddProfiles(typesFromAssemblies.Select(t => t.Assembly).Distinct().ToArray());

    public void ConstructServicesUsing(Func<Type, object> constructor) =>
        ServiceCtor = constructor ?? throw new ArgumentNullException(nameof(constructor));

    public IMapperConfigurationExpression UseMlMemberMatching(Action<MlMatchingOptions>? configure = null)
    {
        MlMatching.Enabled = true;
        configure?.Invoke(MlMatching);
        return this;
    }
}
