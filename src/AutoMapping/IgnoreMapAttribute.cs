namespace AutoMapping;

/// <summary>Excludes a destination property or field from automatic mapping.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class IgnoreMapAttribute : Attribute
{
}
