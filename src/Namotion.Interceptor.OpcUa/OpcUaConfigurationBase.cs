using Namotion.Interceptor.Sources.Paths;
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
    public required ISourcePathProvider PathProvider { get; init; }

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
    /// Gets or sets whether to enable live synchronization of address space changes.
    /// When enabled, local attach/detach events and remote ModelChangeEvents trigger bidirectional sync.
    /// Default is false.
    /// </summary>
    public bool EnableLiveSync { get; init; } = false;

    /// <summary>
    /// Gets or sets whether to enable remote node management operations (AddNodes/DeleteNodes).
    /// When enabled, local changes attempt to modify the remote address space structure.
    /// For clients: Calls AddNodes/DeleteNodes on the server (if supported).
    /// For servers: Accepts AddNodes/DeleteNodes requests from external clients.
    /// Default is false.
    /// </summary>
    public bool EnableRemoteNodeManagement { get; init; } = false;

    /// <summary>
    /// Gets or sets whether to enable periodic address space resync as a fallback.
    /// When enabled, the entire address space is periodically compared and synchronized.
    /// Useful when ModelChangeEvents are not supported or may be missed.
    /// Default is false.
    /// </summary>
    public bool EnablePeriodicResync { get; init; } = false;

    /// <summary>
    /// Gets or sets the interval for periodic address space resynchronization.
    /// Only used when EnablePeriodicResync is true.
    /// Default is 30 seconds.
    /// </summary>
    public TimeSpan PeriodicResyncInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the time window to buffer incoming changes (default: 8ms).
    /// </summary>
    public TimeSpan? BufferTime { get; init; }

    /// <summary>
    /// Gets or sets the retry time (default: 10s).
    /// </summary>
    public TimeSpan? RetryTime { get; init; }
}
