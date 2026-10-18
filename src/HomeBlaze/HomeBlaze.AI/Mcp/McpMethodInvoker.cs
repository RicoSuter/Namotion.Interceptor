using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Resolves the subject of an MCP tool and calls a query or operation method of it: checks the method kind, the
/// operation filter and whether confirmation is required, converts the arguments, invokes the method and turns the
/// outcome into the tool result. invoke_query, invoke_operation and the Markdown tools share it.
/// </summary>
internal sealed class McpMethodInvoker
{
    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly PathProviderBase _pathProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;
    private readonly bool _isReadOnly;
    private readonly Func<RegisteredSubject, MethodMetadata, string?>? _operationFilter;

    public McpMethodInvoker(
        Func<IInterceptorSubject> rootSubjectProvider,
        PathProviderBase pathProvider,
        IServiceProvider serviceProvider,
        ILogger logger,
        bool isReadOnly,
        Func<RegisteredSubject, MethodMetadata, string?>? operationFilter)
    {
        _rootSubjectProvider = rootSubjectProvider;
        _pathProvider = pathProvider;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _isReadOnly = isReadOnly;
        _operationFilter = operationFilter;
    }

    /// <summary>
    /// Resolves the subject whose path is the string argument <paramref name="pathArgument"/> of the tool input: the root
    /// for an empty path or "/".
    /// </summary>
    /// <param name="input">The tool input.</param>
    /// <param name="pathArgument">The name of the argument that holds the path.</param>
    /// <param name="path">The path, or empty when the argument is missing.</param>
    /// <param name="error">The result for the client when null is returned: the argument is missing or not a string, or no subject is at the path.</param>
    public RegisteredSubject? TryResolveSubject(JsonElement input, string pathArgument, out string path, out object? error)
    {
        if (!input.TryGetProperty(pathArgument, out var pathElement))
        {
            path = string.Empty;
            error = new { error = $"Missing parameter '{pathArgument}'." };
            return null;
        }

        if (pathElement.ValueKind != JsonValueKind.String)
        {
            path = string.Empty;
            error = new { error = $"Parameter '{pathArgument}' must be a string." };
            return null;
        }

        path = pathElement.GetString()!;
        var subject = _rootSubjectProvider().TryGetRegisteredSubject();
        if (subject is not null && !string.IsNullOrEmpty(path) && path != "/")
        {
            subject = _pathProvider.TryGetSubjectFromPath(subject, path);
        }

        error = subject is null ? new { error = "Subject not found." } : null;
        return subject;
    }

    /// <summary>
    /// Invokes the method and returns <c>{ success, result }</c>, or <c>{ error }</c> when the call is refused, an argument
    /// does not fit or the method fails.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled during the call.</exception>
    /// <param name="subject">The subject that has the method.</param>
    /// <param name="methodName">The registered method name.</param>
    /// <param name="allowedKind">The kind of method the calling tool may invoke.</param>
    /// <param name="argumentsElement">The arguments object by parameter name; undefined or null when none are given.</param>
    /// <param name="cancellationToken">Passed to the method.</param>
    public async Task<object?> InvokeAsync(
        RegisteredSubject subject, string methodName, MethodKind allowedKind, JsonElement argumentsElement, CancellationToken cancellationToken)
    {
        var methodProperty = subject.TryGetProperty(methodName);
        // A method only for people is not offered to agents, so it is unknown to them.
        if (methodProperty?.TryGetMethodMetadata() is not { } method || !method.IsFor(MethodAudience.Agents))
        {
            return new { error = $"Method not found: {methodName}" };
        }

        var callError = GetCallError(subject, methodName, method, allowedKind);
        if (callError is not null)
        {
            return new { error = callError };
        }

        var inputParameters = method.Parameters.Where(parameter => parameter.RequiresInput).ToArray();
        var (arguments, argumentError) = ReadArguments(argumentsElement, inputParameters);
        if (argumentError is not null)
        {
            return new { error = argumentError };
        }

        try
        {
            var result = MethodResultLimit.Apply(await method.InvokeAsync(arguments, _serviceProvider, cancellationToken));
            return result is not null ? new { success = true, result } : new { success = true };
        }
        catch (ArgumentException exception)
        {
            // Argument validation messages are written for the caller, for example listing the known values.
            _logger.LogWarning(exception, "Method '{MethodName}' rejected an argument.", methodName);
            return new { error = exception.Message };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to invoke method '{MethodName}' on subject.", methodName);
            return new { error = "Method invocation failed. Check server logs for details." };
        }
    }

    private string? GetCallError(RegisteredSubject subject, string methodName, MethodMetadata method, MethodKind allowedKind)
    {
        if (method.Kind != allowedKind)
        {
            return DescribeWrongKind(methodName, method.Kind);
        }

        if (method.Kind == MethodKind.Operation && _operationFilter?.Invoke(subject, method) is { } filterError)
        {
            return filterError;
        }

        if (method.RequiresConfirmation)
        {
            return $"'{methodName}' requires confirmation by a person and cannot be called through MCP; run it from the HomeBlaze UI.";
        }

        return null;
    }

    private string DescribeWrongKind(string methodName, MethodKind kind) => kind switch
    {
        MethodKind.Query => $"'{methodName}' is a query; call it with {HomeBlazeMcpToolProvider.InvokeQueryToolName}.",
        _ when _isReadOnly => $"'{methodName}' is an operation; it can only be called with {HomeBlazeMcpToolProvider.InvokeOperationToolName}, which is not available in read-only mode.",
        _ => $"'{methodName}' is an operation; call it with {HomeBlazeMcpToolProvider.InvokeOperationToolName}."
    };

    private static (object?[]? Arguments, string? Error) ReadArguments(JsonElement argumentsElement, MethodParameter[] inputParameters)
    {
        var argumentsError = GetArgumentsError(argumentsElement, inputParameters);
        if (argumentsError is not null)
        {
            return (null, argumentsError);
        }

        var hasArguments = argumentsElement.ValueKind == JsonValueKind.Object;

        var arguments = new object?[inputParameters.Length];
        for (var i = 0; i < inputParameters.Length; i++)
        {
            var parameter = inputParameters[i];
            if (!hasArguments || !argumentsElement.TryGetProperty(parameter.Name, out var argumentValue))
            {
                // A missing non-nullable argument would otherwise reach the method as default, for example a volume of 0.
                if (!parameter.IsNullable)
                {
                    return (null, $"Missing parameter '{parameter.Name}'. {DescribeExpectedParameters(inputParameters)}");
                }

                continue;
            }

            try
            {
                arguments[i] = McpValueConverter.Deserialize(argumentValue, parameter.Type);
            }
            catch (JsonException exception)
            {
                return (null, $"Invalid value for parameter '{parameter.Name}': {exception.Message}");
            }
        }

        return (arguments, null);
    }

    // No arguments, given as undefined or null, are valid; an object may only name input parameters.
    private static string? GetArgumentsError(JsonElement argumentsElement, MethodParameter[] inputParameters)
    {
        if (argumentsElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        if (argumentsElement.ValueKind != JsonValueKind.Object)
        {
            return $"'parameters' must be an object. {DescribeExpectedParameters(inputParameters)}";
        }

        foreach (var argument in argumentsElement.EnumerateObject())
        {
            if (!inputParameters.Any(parameter => parameter.Name == argument.Name))
            {
                return $"Unknown parameter '{argument.Name}'. {DescribeExpectedParameters(inputParameters)}";
            }
        }

        return null;
    }

    private static string DescribeExpectedParameters(MethodParameter[] inputParameters) =>
        inputParameters.Length == 0
            ? "The method takes no parameters."
            : $"Expected parameters: {string.Join(", ", inputParameters.Select(parameter => parameter.Name))}.";
}
