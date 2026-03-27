using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Registry property value for methods exposed as dynamic properties.
/// Contains metadata and invocation capability, bound to a specific subject instance.
/// </summary>
public class MethodMetadata
{
    private readonly IInterceptorSubject _subject;
    private readonly Func<object, object?[]?, Task<object?>> _invoke;

    /// <param name="subject">The subject instance this method is bound to.</param>
    /// <param name="invoke">The delegate that performs the actual method invocation.</param>
    public MethodMetadata(IInterceptorSubject subject, Func<object, object?[]?, Task<object?>> invoke)
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
    /// Invokes the method on the bound subject with the provided user-input parameters.
    /// <see cref="MethodParameter.IsRuntimeProvided"/> parameters (e.g., <see cref="CancellationToken"/>)
    /// and <see cref="MethodParameter.IsFromServices"/> parameters are resolved automatically.
    /// </summary>
    /// <param name="parameters">Only parameters where <see cref="MethodParameter.RequiresInput"/> is true.</param>
    /// <param name="serviceProvider">Service provider for resolving [FromServices] parameters.</param>
    /// <param name="cancellationToken">Cancellation token injected into runtime-provided parameters.</param>
    public Task<object?> InvokeAsync(
        object?[]? parameters,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        var resolved = ResolveParameters(parameters, serviceProvider, cancellationToken);
        return _invoke(_subject, resolved);
    }

    private object?[] ResolveParameters(
        object?[]? userParameters,
        IServiceProvider? serviceProvider,
        CancellationToken cancellationToken)
    {
        if (Parameters.Length == 0)
        {
            return [];
        }

        var resolved = new object?[Parameters.Length];
        var userIndex = 0;

        for (var i = 0; i < Parameters.Length; i++)
        {
            var parameter = Parameters[i];
            if (parameter.IsRuntimeProvided)
            {
                resolved[i] = parameter.Type == typeof(CancellationToken)
                    ? cancellationToken
                    : null;
            }
            else if (parameter.IsFromServices)
            {
                resolved[i] = serviceProvider?.GetService(parameter.Type);
            }
            else
            {
                resolved[i] = userParameters != null && userIndex < userParameters.Length
                    ? userParameters[userIndex++]
                    : null;
            }
        }

        return resolved;
    }
}
