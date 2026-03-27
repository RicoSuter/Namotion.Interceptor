namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Registry property value for methods exposed as dynamic properties.
/// Contains metadata and invocation capability, bound to a specific subject instance.
/// </summary>
public class MethodMetadata
{
    private readonly object _subject;
    private readonly Func<object, object?[]?, Task<object?>> _invoke;

    /// <param name="subject">The subject instance this method is bound to.</param>
    /// <param name="invoke">The delegate that performs the actual method invocation.</param>
    public MethodMetadata(object subject, Func<object, object?[]?, Task<object?>> invoke)
    {
        _subject = subject;
        _invoke = invoke;
    }

    /// <summary>
    /// Whether this is an <see cref="MethodKind.Operation"/> or <see cref="MethodKind.Query"/>.
    /// </summary>
    public MethodKind Kind { get; init; }

    /// <summary>
    /// The display title shown in the UI.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// An optional longer description of what the method does.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// An optional icon identifier for UI rendering.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Display order position (lower values appear first).
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// Whether the UI should show a confirmation dialog before invoking this method.
    /// </summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>
    /// The method's parameter descriptors.
    /// </summary>
    public MethodParameter[] Parameters { get; init; } = [];

    /// <summary>
    /// The unwrapped return type (null for void/Task methods).
    /// For <c>Task&lt;T&gt;</c> return types this is <c>T</c>.
    /// </summary>
    public Type? ResultType { get; init; }

    /// <summary>
    /// Invokes the method on the bound subject with the provided parameters.
    /// </summary>
    public Task<object?> InvokeAsync(object?[]? parameters = null)
    {
        return _invoke(_subject, parameters);
    }
}
