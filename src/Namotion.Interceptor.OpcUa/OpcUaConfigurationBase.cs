using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Registry.Paths;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa;

/// <summary>
/// Base configuration class shared between OPC UA client and server.
/// Contains common settings for value conversion, type resolution, and dynamic property handling.
/// </summary>
public abstract class OpcUaConfigurationBase
{
    /// <summary>
    /// Gets the source path provider used to map between OPC UA node browse names and C# property names.
    /// This provider determines which properties are included and how their names are translated.
    /// </summary>
    public required IPathProvider PathProvider { get; init; }

    /// <summary>
    /// Gets the value converter used to convert between OPC UA node values and C# property values.
    /// Handles type conversions such as decimal to double for OPC UA compatibility.
    /// </summary>
    public required OpcUaValueConverter ValueConverter { get; init; }

    /// <summary>
    /// Gets the subject factory used to create interceptor subject instances for OPC UA object nodes.
    /// Required for clients that need to create subjects for dynamically discovered nodes.
    /// </summary>
    public OpcUaSubjectFactory? SubjectFactory { get; init; }

    /// <summary>
    /// Gets or sets an async predicate that is called when an unknown (not statically typed) OPC UA node or variable is found during browsing.
    /// If the function returns true, the node is added as a dynamic property to the given subject.
    /// Default is add all missing as dynamic properties.
    /// </summary>
    public Func<ReferenceDescription, CancellationToken, Task<bool>>? ShouldAddDynamicProperty { get; init; } = 
        static (_, _) => Task.FromResult(true);

    /// <summary>
    /// Enables bidirectional synchronization of address space structure.
    /// Server: Creates/removes OPC UA nodes on attach/detach AND fires ModelChangeEvents.
    /// Client: Subscribes to ModelChangeEvents AND creates local subjects for new nodes.
    /// Default: true (recommended for most use cases).
    /// </summary>
    public bool EnableStructureSynchronization { get; set; } = true;

    /// <summary>
    /// Enables periodic full resynchronization as a fallback mechanism.
    /// Useful when ModelChangeEvents may be missed or are not supported.
    /// Default: false.
    /// </summary>
    public bool EnablePeriodicResynchronization { get; set; } = false;

    /// <summary>
    /// The interval for periodic resynchronization.
    /// Only used when EnablePeriodicResynchronization is true.
    /// Default: 30 seconds.
    /// </summary>
    public TimeSpan PeriodicResynchronizationInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the time window to buffer incoming changes (default: 8ms).
    /// </summary>
    public TimeSpan? BufferTime { get; init; }

    /// <summary>
    /// Gets or sets the retry time (default: 10s).
    /// </summary>
    public TimeSpan? RetryTime { get; init; }

    /// <summary>
    /// Validates common configuration values shared between client and server.
    /// Derived classes should call this method and add their own validations.
    /// </summary>
    protected void ValidateBase()
    {
        ArgumentNullException.ThrowIfNull(PathProvider);
        ArgumentNullException.ThrowIfNull(ValueConverter);

        if (EnablePeriodicResynchronization && PeriodicResynchronizationInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentException(
                $"PeriodicResynchronizationInterval must be at least 1 second when EnablePeriodicResynchronization is true (got: {PeriodicResynchronizationInterval.TotalSeconds}s)",
                nameof(PeriodicResynchronizationInterval));
        }
    }
}
