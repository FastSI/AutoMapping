using System.Linq.Expressions;
using System.Reflection;

namespace AutoMapping.Internal;

/// <summary>Effective settings of one profile: profile values override the root configuration, lists are merged.</summary>
internal sealed class ProfileSettings
{
    public ProfileSettings(Profile profile, MapperConfigurationExpression root)
    {
        ProfileName = profile.ProfileName;
        AllowNullCollections = profile.AllowNullCollections ?? root.AllowNullCollections ?? false;
        AllowNullDestinationValues = profile.AllowNullDestinationValues ?? root.AllowNullDestinationValues ?? true;
        CreateMissingTypeMaps = profile.CreateMissingTypeMaps ?? root.CreateMissingTypeMaps ?? false;
        ShouldUseConstructor = profile.ShouldUseConstructor ?? root.ShouldUseConstructor ?? (_ => true);

        var isRoot = ReferenceEquals(profile, root);
        IEnumerable<T> Merge<T>(Func<Profile, IEnumerable<T>> select) =>
            isRoot ? select(root) : select(root).Concat(select(profile));
        // ClearPrefixes() on a profile also drops the inherited prefix/postfix lists
        IEnumerable<T> MergeAffixes<T>(Func<Profile, IEnumerable<T>> select) =>
            isRoot || profile.PrefixesCleared ? select(profile) : select(root).Concat(select(profile));

        GlobalIgnores = Merge(p => p.GlobalIgnores).ToArray();
        ValueTransformers = Merge(p => p.Transformers.Items).ToList();

        Matcher = new MemberMatcher(
            MergeAffixes(p => p.SourcePrefixes), MergeAffixes(p => p.SourcePostfixes),
            MergeAffixes(p => p.DestinationPrefixes), MergeAffixes(p => p.DestinationPostfixes),
            Merge(p => p.Replacements),
            profile.ShouldMapProperty ?? root.ShouldMapProperty,
            profile.ShouldMapField ?? root.ShouldMapField,
            profile.ShouldMapMethod ?? root.ShouldMapMethod,
            profile.DestinationMemberNamingConvention ?? root.DestinationMemberNamingConvention,
            profile.SourceMemberNamingConvention ?? root.SourceMemberNamingConvention,
            Merge(p => p.Aliases));
    }

    public string ProfileName { get; }
    public bool AllowNullCollections { get; }
    public bool AllowNullDestinationValues { get; }
    public bool CreateMissingTypeMaps { get; }
    public Func<ConstructorInfo, bool> ShouldUseConstructor { get; }
    public string[] GlobalIgnores { get; }
    public List<(Type Type, LambdaExpression Transformer)> ValueTransformers { get; }
    public MemberMatcher Matcher { get; }
}
