using System.Linq.Expressions;
using System.Reflection;

namespace AutoMapping.Internal;

internal readonly record struct TypePair(Type Source, Type Destination)
{
    public override string ToString() => $"{TypeHelpers.Pretty(Source)} -> {TypeHelpers.Pretty(Destination)}";
}

internal enum ResolverKind
{
    /// <summary><c>Func&lt;TSource, TDestination, TMember, ResolutionContext, TResult&gt;</c></summary>
    Plain,
    /// <summary><c>Func&lt;TSource, TDestination, TSourceMember, TMember, ResolutionContext, TResult&gt;</c></summary>
    SourceMember,
    /// <summary><c>Func&lt;TSourceMember, ResolutionContext, TMember&gt;</c></summary>
    Converter
}

/// <summary>Everything known about how one destination member (or constructor parameter) is filled.</summary>
internal sealed class PropertyMap
{
    public PropertyMap(string name, Type type, MemberInfo? member)
    {
        DestinationName = name;
        DestinationType = type;
        DestinationMember = member;
    }

    public string DestinationName { get; }
    public Type DestinationType { get; }
    public MemberInfo? DestinationMember { get; }

    /// <summary>Parents to walk (and create if null) before assigning <see cref="DestinationMember"/> (ForPath).</summary>
    public IReadOnlyList<MemberInfo>? DestinationPath { get; set; }

    public bool Ignored { get; set; }
    public LambdaExpression? CustomSource { get; set; }

    public Delegate? Resolver { get; set; }
    public ResolverKind ResolverKind { get; set; }
    /// <summary>Source member for <see cref="ResolverKind.SourceMember"/> / <see cref="ResolverKind.Converter"/>; null means "by convention".</summary>
    public LambdaExpression? ResolverSource { get; set; }

    public bool HasConstant { get; set; }
    public object? Constant { get; set; }
    public Type? ConstantType { get; set; }

    public LambdaExpression? WhenExpression { get; set; }   // MapWhen: also used by ProjectTo
    public Delegate? PreCondition { get; set; }      // Func<TSource, TDestination, ResolutionContext, bool>
    public Delegate? Condition { get; set; }         // Func<TSource, TDestination, TMember, TMember, ResolutionContext, bool>
    public bool HasNullSubstitute { get; set; }
    public object? NullSubstitute { get; set; }

    public bool? AllowNull { get; set; }
    public bool? UseDestinationValue { get; set; }
    public bool MapAtRuntime { get; set; }
    public bool ExplicitExpansion { get; set; }
    public int Order { get; set; }

    public IReadOnlyList<MemberInfo>? SourceChain { get; set; }
    public bool FromMl { get; set; }
    public float MlScore { get; set; }

    /// <summary>Get-only collection/dictionary that is populated in place.</summary>
    public bool IsGetOnlyCollection { get; set; }

    public bool IsResolved =>
        Ignored || CustomSource != null || Resolver != null || HasConstant || SourceChain != null;

    public PropertyMap Clone() => (PropertyMap)MemberwiseClone();
}

/// <summary>Mutable configuration + resolved plan for one source/destination pair.</summary>
internal sealed class TypeMap
{
    private static int _nextId;

    public TypeMap(Type source, Type destination, Profile profile)
    {
        SourceType = source;
        DestinationType = destination;
        Profile = profile;
        Id = Interlocked.Increment(ref _nextId);
    }

    public int Id { get; }
    public Type SourceType { get; }
    public Type DestinationType { get; }
    public TypePair Pair => new(SourceType, DestinationType);
    public Profile Profile { get; }
    public ProfileSettings Settings { get; set; } = null!;

    /// <summary>Set for maps created by ReverseMap(): the forward map, used to un-flatten conventional member maps.</summary>
    public TypeMap? ReverseOf { get; set; }

    // ---- configuration (filled by the fluent API) -------------------------------------------------------
    public MemberList MemberList { get; set; } = MemberList.Destination;
    public Dictionary<string, PropertyMap> Overrides { get; } = new(StringComparer.Ordinal);   // member name or "A.B" path
    public Dictionary<string, PropertyMap> CtorParamOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Action<PropertyMap>> ForAllMembers { get; } = new();
    public List<Action<PropertyMap>> ForAllOtherMembers { get; } = new();
    public List<LambdaExpression> IncludedMembers { get; } = new();
    public HashSet<string> SourceMembersNotValidated { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Delegate> BeforeMaps { get; } = new();      // Action<TSource, TDestination, ResolutionContext>
    public List<Delegate> AfterMaps { get; } = new();
    public Delegate? ConstructUsing { get; set; }           // Func<TSource, ResolutionContext, TDestination>
    public bool ConstructUsingServiceLocator { get; set; }
    public Delegate? CustomConverter { get; set; }          // Func<TSource, TDestination, ResolutionContext, TDestination>
    public Type? ConverterType { get; set; }                // ITypeConverter<,> implementation (closed or open generic)
    public LambdaExpression? ConvertExpression { get; set; }    // ConvertUsing(expression): also used by ProjectTo
    public Delegate? EqualityComparison { get; set; }           // Func<TSource, TDestination, bool>
    public LambdaExpression? EqualityExpression { get; set; }   // same predicate as an expression: Persist() sends it to the query provider
    public bool ProjectionOnly { get; set; }
    public bool IsInline { get; set; }
    public List<string> EnumUnmapped { get; } = new();
    public List<TypePair> Includes { get; } = new();
    public List<TypePair> IncludeBases { get; } = new();
    public bool IncludeAllDerived { get; set; }
    public Type? AsType { get; set; }
    public bool PreserveReferences { get; set; }
    public int MaxDepth { get; set; }
    public bool IgnoreUnmapped { get; set; }
    public bool DisableCtorValidation { get; set; }
    public List<(Type Type, LambdaExpression Transformer)> ValueTransformers { get; } = new();

    // ---- resolved plan (filled by MapperConfiguration) --------------------------------------------------
    public bool Finished { get; set; }
    public List<TypeMap> BaseMaps { get; } = new();
    public List<TypeMap> DerivedTypeMaps { get; } = new();
    public List<PropertyMap> PropertyMaps { get; } = new();
    public ConstructorInfo? Constructor { get; set; }
    public List<PropertyMap> CtorParams { get; } = new();
    public string? ConstructorError { get; set; }

    public bool HasCustomConversion => CustomConverter != null || ConverterType != null || AsType != null;

    public IEnumerable<PropertyMap> Unmapped => PropertyMaps.Where(p => !p.IsResolved);
}

/// <summary>Open generic map such as <c>CreateMap(typeof(Page&lt;&gt;), typeof(PageDto&lt;&gt;))</c>, closed on demand.</summary>
internal sealed class OpenGenericMap
{
    public OpenGenericMap(Type source, Type destination, Profile profile)
    {
        Source = source;
        Destination = destination;
        Profile = profile;
    }

    public Type Source { get; }
    public Type Destination { get; }
    public Profile Profile { get; }
    public MemberList MemberList { get; set; } = MemberList.Destination;
    public Type? ConverterType { get; set; }
    public bool IncludeAllDerived { get; set; }
    public bool PreserveReferences { get; set; }
    public int MaxDepth { get; set; }
    public bool IgnoreUnmapped { get; set; }
}
