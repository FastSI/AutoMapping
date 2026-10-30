namespace AutoMapping;

/// <summary>Base type for all AutoMapping errors.</summary>
public class AutoMappingException : Exception
{
    public AutoMappingException(string message) : base(message) { }
    public AutoMappingException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Thrown when a mapping cannot be built or is mis-configured (missing map, no constructor, ...).</summary>
public sealed class MappingConfigurationException : AutoMappingException
{
    public MappingConfigurationException(string message) : base(message) { }
}

/// <summary>Thrown while executing a mapping (depth limit exceeded, user delegate failed, ...).</summary>
public sealed class MappingException : AutoMappingException
{
    public Type? SourceType { get; }
    public Type? DestinationType { get; }

    public MappingException(string message, Type? source = null, Type? destination = null, Exception? inner = null)
        : base(message, inner!)
    {
        SourceType = source;
        DestinationType = destination;
    }
}

/// <summary>Thrown by <see cref="MapperConfiguration.AssertConfigurationIsValid()"/>.</summary>
public sealed class ConfigurationValidationException : AutoMappingException
{
    public IReadOnlyList<string> Errors { get; }

    public ConfigurationValidationException(IReadOnlyList<string> errors)
        : base("AutoMapping configuration is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => " - " + e)))
    {
        Errors = errors;
    }
}
