namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Registry property value for methods exposed as dynamic properties.
/// Contains metadata and invocation capability.
/// </summary>
public class MethodMetadata
{
    /// <summary>
    /// Whether this is an <see cref="MethodKind.Operation"/> or <see cref="MethodKind.Query"/>.
    /// </summary>
    public MethodKind Kind { get; set; }

    /// <summary>
    /// The display title shown in the UI.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// An optional longer description of what the method does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// An optional icon identifier for UI rendering.
    /// </summary>
    public string? Icon { get; set; }

    /// <summary>
    /// Display order position (lower values appear first).
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Whether the UI should show a confirmation dialog before invoking this method.
    /// </summary>
    public bool RequiresConfirmation { get; set; }

    /// <summary>
    /// The method's parameter descriptors.
    /// </summary>
    public MethodParameter[] Parameters { get; set; } = [];

    /// <summary>
    /// The unwrapped return type (null for void/Task methods).
    /// For <c>Task&lt;T&gt;</c> return types this is <c>T</c>.
    /// </summary>
    public Type? ResultType { get; set; }

    /// <summary>
    /// Invokes the method on the given subject with the provided parameters.
    /// The first argument is the subject instance, the second is the parameter values.
    /// </summary>
    public required Func<object, object?[]?, Task<object?>> InvokeAsync { get; init; }
}
