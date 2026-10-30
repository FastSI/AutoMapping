using System.Linq.Expressions;
using System.Reflection;
using AutoMapping.Internal;

namespace AutoMapping;

/// <summary>Groups related maps and settings. Derive and call <see cref="CreateMap{TSource,TDestination}()"/> in the constructor.</summary>
public abstract class Profile : IProfileExpression
{
    internal ValueTransformerCollection Transformers { get; } = new();
    internal List<TypeMap> TypeMaps { get; } = new();
    internal List<OpenGenericMap> OpenGenericMaps { get; } = new();
    internal List<string> GlobalIgnores { get; } = new();
    internal List<string> SourcePrefixes { get; } = new();
    internal List<string> SourcePostfixes { get; } = new();
    internal List<string> DestinationPrefixes { get; } = new();
    internal List<string> DestinationPostfixes { get; } = new();
    internal List<(string From, string To)> Replacements { get; } = new();
    internal List<(string Original, string Alias)> Aliases { get; } = new();
    internal List<Action<ITypeMapInfo, IMappingExpression>> AllMapsActions { get; } = new();
    internal bool PrefixesCleared { get; private set; }

    protected Profile() : this(null)
    {
    }

    protected Profile(string? profileName)
    {
        ProfileName = profileName ?? GetType().FullName ?? GetType().Name;
    }

    public string ProfileName { get; }

    public bool? AllowNullCollections { get; set; }
    public bool? AllowNullDestinationValues { get; set; }
    public bool? CreateMissingTypeMaps { get; set; }
    public INamingConvention? SourceMemberNamingConvention { get; set; }
    public INamingConvention? DestinationMemberNamingConvention { get; set; }
    public Func<PropertyInfo, bool>? ShouldMapProperty { get; set; }
    public Func<FieldInfo, bool>? ShouldMapField { get; set; }
    public Func<MethodInfo, bool>? ShouldMapMethod { get; set; }
    public Func<ConstructorInfo, bool>? ShouldUseConstructor { get; set; }

    public IValueTransformerConfiguration ValueTransformers => Transformers;

    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>() =>
        CreateMap<TSource, TDestination>(MemberList.Destination);

    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(MemberList memberList)
    {
        var typeMap = new TypeMap(typeof(TSource), typeof(TDestination), this) { MemberList = memberList };
        TypeMaps.Add(typeMap);
        return new MappingExpression<TSource, TDestination>(typeMap, this);
    }

    public IMappingExpression<TSource, TDestination> CreateProjection<TSource, TDestination>()
    {
        var typeMap = new TypeMap(typeof(TSource), typeof(TDestination), this) { MemberList = MemberList.Destination, ProjectionOnly = true };
        TypeMaps.Add(typeMap);
        return new MappingExpression<TSource, TDestination>(typeMap, this);
    }

    public void ForAllMaps(Action<ITypeMapInfo, IMappingExpression> configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AllMapsActions.Add(configuration);
    }

    public void RecognizeAlias(string original, string alias)
    {
        ArgumentException.ThrowIfNullOrEmpty(original);
        ArgumentException.ThrowIfNullOrEmpty(alias);
        Aliases.Add((original, alias));
    }

    public IMappingExpression CreateMap(Type sourceType, Type destinationType) =>
        CreateMap(sourceType, destinationType, MemberList.Destination);

    public IMappingExpression CreateMap(Type sourceType, Type destinationType, MemberList memberList)
    {
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);

        if (sourceType.IsGenericTypeDefinition || destinationType.IsGenericTypeDefinition)
        {
            if (!sourceType.IsGenericTypeDefinition || !destinationType.IsGenericTypeDefinition)
                throw new MappingConfigurationException("Open generic maps need both types open, e.g. CreateMap(typeof(Page<>), typeof(PageDto<>)).");
            if (sourceType.GetGenericArguments().Length != destinationType.GetGenericArguments().Length)
                throw new MappingConfigurationException("Open generic maps need the same number of type arguments on both sides.");

            var open = new OpenGenericMap(sourceType, destinationType, this) { MemberList = memberList };
            OpenGenericMaps.Add(open);
            return new NonGenericMappingExpression(null, open, this);
        }

        var typeMap = new TypeMap(sourceType, destinationType, this) { MemberList = memberList };
        TypeMaps.Add(typeMap);
        return new NonGenericMappingExpression(typeMap, null, this);
    }

    public void AddGlobalIgnore(string startingwith)
    {
        ArgumentException.ThrowIfNullOrEmpty(startingwith);
        GlobalIgnores.Add(startingwith);
    }

    public void RecognizePrefixes(params string[] prefixes) => SourcePrefixes.AddRange(prefixes);
    public void RecognizePostfixes(params string[] postfixes) => SourcePostfixes.AddRange(postfixes);
    public void RecognizeDestinationPrefixes(params string[] prefixes) => DestinationPrefixes.AddRange(prefixes);
    public void RecognizeDestinationPostfixes(params string[] postfixes) => DestinationPostfixes.AddRange(postfixes);

    public void ClearPrefixes()
    {
        SourcePrefixes.Clear();
        SourcePostfixes.Clear();
        DestinationPrefixes.Clear();
        DestinationPostfixes.Clear();
        PrefixesCleared = true;
    }

    public void ReplaceMemberName(string original, string newValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(original);
        Replacements.Add((original, newValue ?? string.Empty));
    }
}

internal sealed class ValueTransformerCollection : IValueTransformerConfiguration
{
    public List<(Type Type, LambdaExpression Transformer)> Items { get; } = new();

    public void Add<TValue>(Expression<Func<TValue, TValue>> transformer)
    {
        ArgumentNullException.ThrowIfNull(transformer);
        Items.Add((typeof(TValue), transformer));
    }
}
