using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;

namespace AutoMapping;

/// <summary>Which side of a map must be fully covered when validating.</summary>
public enum MemberList
{
    /// <summary>Every destination member must be mapped (default).</summary>
    Destination = 0,
    /// <summary>Every source member must be consumed.</summary>
    Source = 1,
    /// <summary>No validation.</summary>
    None = 2
}

// ------------------------------------------------------------------------------------------------ extension points

/// <summary>Fills a destination member from the whole source. Resolve instances through DI.</summary>
public interface IValueResolver<in TSource, in TDestination, TDestMember>
{
    TDestMember Resolve(TSource source, TDestination destination, TDestMember destMember, ResolutionContext context);
}

/// <summary>Fills a destination member from a specific source member.</summary>
public interface IMemberValueResolver<in TSource, in TDestination, in TSourceMember, TDestMember>
{
    TDestMember Resolve(TSource source, TDestination destination, TSourceMember sourceMember, TDestMember destMember, ResolutionContext context);
}

/// <summary>Converts one source member value to the destination member type.</summary>
public interface IValueConverter<in TSourceMember, out TDestinationMember>
{
    TDestinationMember Convert(TSourceMember sourceMember, ResolutionContext context);
}

/// <summary>Replaces a whole map.</summary>
public interface ITypeConverter<in TSource, TDestination>
{
    /// <param name="source">The source object.</param>
    /// <param name="destination">The existing destination (or default).</param>
    /// <param name="context">The resolution context.</param>
    TDestination Convert(TSource source, TDestination destination, ResolutionContext context);
}

/// <summary>Reusable before/after-map hook.</summary>
public interface IMappingAction<in TSource, in TDestination>
{
    void Process(TSource source, TDestination destination, ResolutionContext context);
}

/// <summary>Splits member names into words for flattening (<c>AddressCity</c> to <c>Address</c> + <c>City</c>).</summary>
public interface INamingConvention
{
    Regex SplittingExpression { get; }
    string SeparatorCharacter { get; }
}

public sealed class PascalCaseNamingConvention : INamingConvention
{
    public Regex SplittingExpression { get; } = new(@"(\p{Lu}+(?=$|\p{Lu}[\p{Ll}0-9])|\p{Lu}?[\p{Ll}0-9]+)", RegexOptions.Compiled);
    public string SeparatorCharacter => string.Empty;
}

public sealed class LowerUnderscoreNamingConvention : INamingConvention
{
    public Regex SplittingExpression { get; } = new(@"[\p{Ll}\p{Lu}0-9]+(?=_?)", RegexOptions.Compiled);
    public string SeparatorCharacter => "_";
}

// ------------------------------------------------------------------------------------------------ member options

/// <summary>Per destination member configuration.</summary>
public interface IMemberConfigurationExpression<TSource, TDestination, TMember>
{
    /// <summary>Map from a source expression. Null-propagation through the chain is handled for you; ProjectTo can translate it.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Expression<Func<TSource, TResult>> sourceMember);
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Func<TSource, TDestination, TResult> resolver);
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Func<TSource, TDestination, TMember, TResult> resolver);
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TResult>(Func<TSource, TDestination, TMember, ResolutionContext, TResult> resolver);
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom(IValueResolver<TSource, TDestination, TMember> resolver);
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TValueResolver>()
        where TValueResolver : IValueResolver<TSource, TDestination, TMember>;
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TValueResolver, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        where TValueResolver : IMemberValueResolver<TSource, TDestination, TSourceMember, TMember>;
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom<TValueResolver, TSourceMember>(string sourceMemberName)
        where TValueResolver : IMemberValueResolver<TSource, TDestination, TSourceMember, TMember>;
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapFrom(string sourceMemberName);

    /// <summary>Convert the (conventionally matched or given) source member with a value converter.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TValueConverter, TSourceMember>(Expression<Func<TSource, TSourceMember>> sourceMember)
        where TValueConverter : IValueConverter<TSourceMember, TMember>;
    IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TValueConverter, TSourceMember>(string sourceMemberName)
        where TValueConverter : IValueConverter<TSourceMember, TMember>;
    IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TValueConverter, TSourceMember>()
        where TValueConverter : IValueConverter<TSourceMember, TMember>;
    IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TSourceMember>(
        IValueConverter<TSourceMember, TMember> converter, Expression<Func<TSource, TSourceMember>> sourceMember);
    IMemberConfigurationExpression<TSource, TDestination, TMember> ConvertUsing<TSourceMember>(IValueConverter<TSourceMember, TMember> converter);

    /// <summary>Assign a constant.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> UseValue<TValue>(TValue value);

    IMemberConfigurationExpression<TSource, TDestination, TMember> Ignore();

    /// <summary>Map into the existing destination value (nested object / collection) instead of replacing it.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> UseDestinationValue();
    IMemberConfigurationExpression<TSource, TDestination, TMember> DoNotUseDestinationValue();

    /// <summary>Allow a null source to produce a null destination (overrides the profile default).</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> AllowNull();
    /// <summary>Replace a null result with an empty string / empty collection / new instance.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> DoNotAllowNull();

    /// <summary>Choose the map from the runtime type of the source value (polymorphic members).</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapAtRuntime();

    /// <summary>ProjectTo only: leave this member out unless it is listed in <c>membersToExpand</c>.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> ExplicitExpansion();

    /// <summary>Members are mapped in ascending order (default 0, then declaration order).</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> SetMappingOrder(int order);

    /// <summary>Evaluated before the value is resolved.</summary>
    /// <summary>
    /// Maps the member only when the predicate holds. Unlike <c>PreCondition</c> the predicate is an expression, so
    /// <c>ProjectTo</c> can translate it (the member becomes <c>default</c> when it is false).
    /// </summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> MapWhen(Expression<Func<TSource, bool>> predicate);
    IMemberConfigurationExpression<TSource, TDestination, TMember> PreCondition(Func<TSource, bool> predicate);
    IMemberConfigurationExpression<TSource, TDestination, TMember> PreCondition(Func<TSource, ResolutionContext, bool> predicate);
    IMemberConfigurationExpression<TSource, TDestination, TMember> PreCondition(Func<TSource, TDestination, ResolutionContext, bool> predicate);

    /// <summary>Evaluated after the value is resolved; the member is assigned only when it returns true.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, bool> predicate);
    IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, TMember, bool> predicate);
    IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, TMember, TMember, bool> predicate);
    IMemberConfigurationExpression<TSource, TDestination, TMember> Condition(Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool> predicate);

    /// <summary>Value used when the resolved value is null.</summary>
    IMemberConfigurationExpression<TSource, TDestination, TMember> NullSubstitute(object nullSubstitute);
}

/// <summary>Configuration of one constructor parameter.</summary>
public interface ICtorParamConfigurationExpression<TSource>
{
    void MapFrom<TResult>(Expression<Func<TSource, TResult>> sourceMember);
    void MapFrom<TResult>(Func<TSource, ResolutionContext, TResult> resolver);
    void MapFrom(string sourceMemberName);
}

/// <summary>Per source member configuration (for <see cref="MemberList.Source"/> validation).</summary>
/// <summary>Read-only description of a map, handed to <c>ForAllMaps</c>.</summary>
public interface ITypeMapInfo
{
    Type SourceType { get; }
    Type DestinationType { get; }
    MemberList MemberList { get; }
}

public interface ISourceMemberConfigurationExpression
{
    void DoNotValidate();
}

// ------------------------------------------------------------------------------------------------ map expressions

/// <summary>Fluent configuration of one source/destination map.</summary>
public interface IMappingExpression<TSource, TDestination>
{
    IMappingExpression<TSource, TDestination> ForMember<TMember>(
        Expression<Func<TDestination, TMember>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> options);

    IMappingExpression<TSource, TDestination> ForMember(
        string name, Action<IMemberConfigurationExpression<TSource, TDestination, object>> options);

    /// <summary>Applies to every destination member (including conventionally matched ones).</summary>
    IMappingExpression<TSource, TDestination> ForAllMembers(Action<IMemberConfigurationExpression<TSource, TDestination, object>> options);

    /// <summary>Applies to every destination member that has no explicit <c>ForMember</c>.</summary>
    IMappingExpression<TSource, TDestination> ForAllOtherMembers(Action<IMemberConfigurationExpression<TSource, TDestination, object>> options);

    /// <summary>Configure a nested destination path, e.g. <c>d =&gt; d.Address.City</c>. Intermediate objects are created.</summary>
    IMappingExpression<TSource, TDestination> ForPath<TMember>(
        Expression<Func<TDestination, TMember>> destinationPath,
        Action<IMemberConfigurationExpression<TSource, TDestination, TMember>> options);

    IMappingExpression<TSource, TDestination> ForCtorParam(string ctorParamName, Action<ICtorParamConfigurationExpression<TSource>> options);

    IMappingExpression<TSource, TDestination> ForSourceMember<TSourceMember>(
        Expression<Func<TSource, TSourceMember>> sourceMember, Action<ISourceMemberConfigurationExpression> options);
    IMappingExpression<TSource, TDestination> ForSourceMember(string sourceMemberName, Action<ISourceMemberConfigurationExpression> options);

    /// <summary>Also look for destination members in these source members (lower priority than the source itself).</summary>
    IMappingExpression<TSource, TDestination> IncludeMembers(params Expression<Func<TSource, object>>[] memberExpressions);

    IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> action);
    IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination, ResolutionContext> action);
    IMappingExpression<TSource, TDestination> BeforeMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>;
    IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> action);
    IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination, ResolutionContext> action);
    IMappingExpression<TSource, TDestination> AfterMap<TMappingAction>() where TMappingAction : IMappingAction<TSource, TDestination>;

    IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, TDestination> factory);
    IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, ResolutionContext, TDestination> factory);
    /// <summary>Create the destination through the service constructor (DI).</summary>
    IMappingExpression<TSource, TDestination> ConstructUsingServiceLocator();

    /// <summary>Converts the whole source with an expression, so <c>ProjectTo</c> can translate it too.</summary>
    IMappingExpression<TSource, TDestination> ConvertUsing(Expression<Func<TSource, TDestination>> mappingExpression);
    IMappingExpression<TSource, TDestination> ConvertUsing(Func<TSource, TDestination, TDestination> converter);
    IMappingExpression<TSource, TDestination> ConvertUsing(Func<TSource, TDestination, ResolutionContext, TDestination> converter);
    IMappingExpression<TSource, TDestination> ConvertUsing(ITypeConverter<TSource, TDestination> converter);
    IMappingExpression<TSource, TDestination> ConvertUsing<TTypeConverter>() where TTypeConverter : ITypeConverter<TSource, TDestination>;

    /// <summary>Create the reverse map, reversing simple member maps and un-flattening flattened ones.</summary>
    IMappingExpression<TDestination, TSource> ReverseMap();

    IMappingExpression<TSource, TDestination> Include<TDerivedSource, TDerivedDestination>()
        where TDerivedSource : TSource where TDerivedDestination : TDestination;
    IMappingExpression<TSource, TDestination> IncludeBase<TBaseSource, TBaseDestination>();
    /// <summary>Use this map for every configured map whose source and destination derive from this map's.</summary>
    IMappingExpression<TSource, TDestination> IncludeAllDerived();
    /// <summary>Map to the implementation type when the destination is an interface/abstract type.</summary>
    IMappingExpression<TSource, TDestination> As<TTarget>() where TTarget : TDestination;

    /// <summary>Track already-mapped instances so cyclic graphs map without recursion.</summary>
    IMappingExpression<TSource, TDestination> PreserveReferences();
    /// <summary>Stop (leave null) when this map is already <paramref name="depth"/> levels deep in the object graph.</summary>
    IMappingExpression<TSource, TDestination> MaxDepth(int depth);
    /// <summary>Do not report constructors that cannot be satisfied during validation.</summary>
    IMappingExpression<TSource, TDestination> DisableCtorValidation();
    /// <summary>Silently skip destination members that have no source (no validation error).</summary>
    IMappingExpression<TSource, TDestination> IgnoreUnmapped();
    IMappingExpression<TSource, TDestination> ValidateMemberList(MemberList memberList);
    /// <summary>
    /// Tells collection mapping how a source item and a destination item are "the same" entity. When mapping onto an existing
    /// destination collection, matching items are updated in place, new items are added and missing items are removed
    /// (the behaviour of AutoMapper.Collection).
    /// </summary>
    IMappingExpression<TSource, TDestination> EqualityComparison(Expression<Func<TSource, TDestination, bool>> equalityComparison);
    /// <summary>Applies to every destination member of type <typeparamref name="TValue"/> after it is mapped.</summary>
    IMappingExpression<TSource, TDestination> AddTransform<TValue>(Expression<Func<TValue, TValue>> transformer);
}

/// <summary>Non-generic configuration, for maps created from runtime types and open generics.</summary>
public interface IMappingExpression
{
    IMappingExpression ForAllMembers(Action<IMemberConfigurationExpression<object, object, object>> options);
    IMappingExpression ForMember(string name, Action<IMemberConfigurationExpression<object, object, object>> options);
    IMappingExpression BeforeMap(Action<object, object> action);
    IMappingExpression AfterMap(Action<object, object> action);
    IMappingExpression ConvertUsing(Type typeConverterType);
    IMappingExpression IncludeAllDerived();
    IMappingExpression PreserveReferences();
    IMappingExpression MaxDepth(int depth);
    IMappingExpression IgnoreUnmapped();
    IMappingExpression ReverseMap();
    /// <summary>Non-generic <c>EqualityComparison</c>: <paramref name="predicate"/> must be a (TSource, TDestination) =&gt; bool lambda.</summary>
    IMappingExpression EqualityComparison(LambdaExpression predicate);
}

public interface IValueTransformerConfiguration
{
    void Add<TValue>(Expression<Func<TValue, TValue>> transformer);
}

/// <summary>Configuration shared by the root configuration and every <see cref="Profile"/>.</summary>
public interface IProfileExpression
{
    IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();
    IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>(MemberList memberList);
    IMappingExpression CreateMap(Type sourceType, Type destinationType);
    IMappingExpression CreateMap(Type sourceType, Type destinationType, MemberList memberList);

    /// <summary>A map used only by <c>ProjectTo</c>. Runtime <c>Map</c> calls on it throw; only translatable features are meaningful.</summary>
    IMappingExpression<TSource, TDestination> CreateProjection<TSource, TDestination>();

    /// <summary>Runs <paramref name="configuration"/> against every map (of this profile, or of all profiles when called on the root).</summary>
    void ForAllMaps(Action<ITypeMapInfo, IMappingExpression> configuration);

    /// <summary>If true a null source collection maps to null; default maps to an empty collection.</summary>
    bool? AllowNullCollections { get; set; }
    /// <summary>If false, null results for strings/collections/objects become empty values. Default true.</summary>
    bool? AllowNullDestinationValues { get; set; }
    /// <summary>If true, missing maps for nested complex types are created on demand instead of throwing.</summary>
    bool? CreateMissingTypeMaps { get; set; }

    INamingConvention? SourceMemberNamingConvention { get; set; }
    INamingConvention? DestinationMemberNamingConvention { get; set; }

    Func<PropertyInfo, bool>? ShouldMapProperty { get; set; }
    Func<FieldInfo, bool>? ShouldMapField { get; set; }
    Func<MethodInfo, bool>? ShouldMapMethod { get; set; }
    Func<ConstructorInfo, bool>? ShouldUseConstructor { get; set; }

    /// <summary>Destination members whose name starts with this prefix are never mapped.</summary>
    void AddGlobalIgnore(string startingwith);
    /// <summary>Source member prefixes ignored when matching (<c>GetName</c> matches <c>Name</c>). Default: <c>Get</c>.</summary>
    void RecognizePrefixes(params string[] prefixes);
    void RecognizePostfixes(params string[] postfixes);
    void RecognizeDestinationPrefixes(params string[] prefixes);
    void RecognizeDestinationPostfixes(params string[] postfixes);
    void ClearPrefixes();
    void ReplaceMemberName(string original, string newValue);
    /// <summary>A source member called <paramref name="original"/> can also be matched by the name <paramref name="alias"/>.</summary>
    void RecognizeAlias(string original, string alias);

    IValueTransformerConfiguration ValueTransformers { get; }
}

/// <summary>Root configuration object passed to the <see cref="MapperConfiguration"/> constructor.</summary>
public interface IMapperConfigurationExpression : IProfileExpression
{
    IMapperConfigurationExpression AddProfile<TProfile>() where TProfile : Profile, new();
    IMapperConfigurationExpression AddProfile(Profile profile);
    IMapperConfigurationExpression AddProfile(Type profileType);
    /// <summary>Registers every concrete <see cref="Profile"/> in the assemblies.</summary>
    IMapperConfigurationExpression AddMaps(params Assembly[] assemblies);
    IMapperConfigurationExpression AddMaps(params Type[] typesFromAssemblies);
    IMapperConfigurationExpression AddProfiles(params Assembly[] assemblies);

    /// <summary>How resolvers, converters and actions are created. Default: <see cref="Activator"/>. DI sets this for you.</summary>
    void ConstructServicesUsing(Func<Type, object> constructor);

    /// <summary>Maximum nesting depth before a <see cref="MappingException"/> is thrown (cycle protection).</summary>
    int MaxDepth { get; set; }

    /// <summary>If true, <c>ProjectTo</c> guards nested member chains against null (<c>a.B == null ? default : a.B.C</c>). Default false, like AutoMapper.</summary>
    bool EnableNullPropagationForQueryMapping { get; set; }

    /// <summary>If false, maps created on the fly (<c>CreateMissingTypeMaps</c>) are not checked by <c>AssertConfigurationIsValid</c>. Default true.</summary>
    bool ValidateInlineMaps { get; set; }

    /// <summary>Makes <c>AssertConfigurationIsValid</c> report enum mappings (<c>ConvertUsingEnumMapping</c>) that leave source values unmapped.</summary>
    void EnableEnumMappingValidation();

    /// <summary>Collection equivalency (<c>EqualityComparison</c>) is always on; kept so AutoMapper.Collection setups compile unchanged.</summary>
    IMapperConfigurationExpression AddCollectionMappers();

    /// <summary>Enable ML.NET fuzzy member matching for destination members that have no exact match.</summary>
    IMapperConfigurationExpression UseMlMemberMatching(Action<MlMatchingOptions>? configure = null);
}

/// <summary>Options for ML.NET based fuzzy member matching.</summary>
public sealed class MlMatchingOptions
{
    /// <summary>Turn fuzzy matching on. Off by default. Runs once at startup, never on the mapping hot path.</summary>
    public bool Enabled { get; set; }

    /// <summary>Minimum cosine similarity (0..1) between name feature vectors.</summary>
    public float Threshold { get; set; } = 0.85f;

    /// <summary>Best candidate must beat the runner-up by this much, otherwise the match is considered ambiguous and skipped.</summary>
    public float MinMargin { get; set; } = 0.03f;
}

// ------------------------------------------------------------------------------------------------ mapper-level

/// <summary>Per-call options for <c>mapper.Map(source, opts =&gt; ...)</c>.</summary>
public interface IMappingOperationOptions<TSource, TDestination>
{
    /// <summary>Values handed to resolvers, converters and hooks through <see cref="ResolutionContext.Items"/>.</summary>
    IDictionary<string, object?> Items { get; }
    /// <summary>Runs on the top-level map before its members are mapped.</summary>
    void BeforeMap(Action<TSource, TDestination> beforeFunction);
    /// <summary>Runs on the top-level map after its members are mapped.</summary>
    void AfterMap(Action<TSource, TDestination> afterFunction);
    /// <summary>Override how resolvers/converters are created for this call.</summary>
    void ConstructServicesUsing(Func<Type, object> constructor);
}

public interface IMappingOperationOptions : IMappingOperationOptions<object, object>
{
}

/// <summary>Read-only view of a built configuration.</summary>
public interface IConfigurationProvider
{
    /// <summary>Throws <see cref="ConfigurationValidationException"/> listing every problem found.</summary>
    void AssertConfigurationIsValid();
    void AssertConfigurationIsValid(string profileName);
    void AssertConfigurationIsValid<TProfile>() where TProfile : Profile;
    /// <summary>Compiles every configured map now (startup warm-up).</summary>
    void CompileMappings();
    /// <summary>The expression tree a map executes; handy for debugging and performance work.</summary>
    LambdaExpression BuildExecutionPlan(Type sourceType, Type destinationType);
    /// <summary>Human readable description of how each destination member is filled.</summary>
    string GetMappingPlan(Type sourceType, Type destinationType);
    IMapper CreateMapper();
    IMapper CreateMapper(Func<Type, object> serviceCtor);
}
