using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Mcp.Models;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Provides the list_methods, invoke_query and invoke_operation tools for HomeBlaze subjects. invoke_operation is
/// only offered when the configuration is not read-only.
/// </summary>
public class HomeBlazeMcpToolProvider : IMcpToolProvider
{
    /// <summary>The name of the tool that calls query methods.</summary>
    public const string InvokeQueryToolName = "invoke_query";

    /// <summary>The name of the tool that calls operation methods.</summary>
    public const string InvokeOperationToolName = "invoke_operation";

    private const string ListMethodsDescription =
        "List the methods of a subject, with each parameter's type, format and unit. Every method is either a query " +
        "(reads, no side effects; call it with invoke_query) or an operation (changes devices or sends something; " +
        "call it with invoke_operation). The kind field of each method says which. Operations with requiresConfirmation " +
        "set need a person's confirmation and cannot be called through MCP.";

    private const string ReadOnlyListMethodsDescription =
        "List the methods of a subject, with each parameter's type, format and unit. Every method is either a query " +
        "(reads, no side effects; call it with invoke_query) or an operation (changes devices or sends something). " +
        "The kind field of each method says which; operations are listed for information but cannot be called on this server.";

    private const string InvokeQueryDescription =
        "Call a query method of a subject: it reads and has no side effects, for example ReadEvaluatedContent on a " +
        "Markdown page or document (*.md) to read it, with includeSourcePaths=true to see where each value comes from. " +
        "Find methods and their kind with list_methods.";

    private const string InvokeOperationDescription =
        "Call an operation method of a subject: it can change devices or send something, for example SendNotification. " +
        "Operations that require confirmation by a person cannot be called. Find methods and their kind with list_methods. " +
        HomeBlazeMcpInstructions.ChangeCaution;

    private static readonly JsonElement ListMethodsSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { path = new { type = "string", description = "Subject path" } },
        required = new[] { "path" }
    });

    private static readonly JsonElement InvokeMethodSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            path = new { type = "string", description = "Subject path" },
            method = new { type = "string", description = "Method name" },
            parameters = new { type = "object", description = "Arguments by parameter name, in the type, format and unit list_methods gives (optional)" }
        },
        required = new[] { "path", "method" }
    });

    private readonly Func<IInterceptorSubject> _rootSubjectProvider;
    private readonly PathProviderBase _pathProvider;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HomeBlazeMcpToolProvider> _logger;
    private readonly bool _isReadOnly;
    private readonly Func<RegisteredSubject, MethodMetadata, string?>? _operationFilter;

    /// <param name="rootSubjectProvider">Resolves the root subject when a tool runs.</param>
    /// <param name="pathProvider">Resolves subject paths.</param>
    /// <param name="serviceProvider">Services for [FromServices] method parameters.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="isReadOnly">Whether invoke_operation is left out.</param>
    /// <param name="operationFilter">Decides whether invoke_operation may call an operation of a subject: returns
    /// null to allow it, or the error message for the client, in which case nothing is invoked. Null allows all.</param>
    public HomeBlazeMcpToolProvider(
        Func<IInterceptorSubject> rootSubjectProvider,
        PathProviderBase pathProvider,
        IServiceProvider serviceProvider,
        ILogger<HomeBlazeMcpToolProvider> logger,
        bool isReadOnly,
        Func<RegisteredSubject, MethodMetadata, string?>? operationFilter = null)
    {
        _operationFilter = operationFilter;
        _rootSubjectProvider = rootSubjectProvider;
        _pathProvider = pathProvider;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _isReadOnly = isReadOnly;
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = "list_methods",
            Description = _isReadOnly ? ReadOnlyListMethodsDescription : ListMethodsDescription,
            InputSchema = ListMethodsSchema,
            Handler = HandleListMethodsAsync,
            Annotations = McpToolAnnotations.ReadOnly
        };

        yield return new McpToolInfo
        {
            Name = InvokeQueryToolName,
            Description = InvokeQueryDescription,
            InputSchema = InvokeMethodSchema,
            Handler = (input, cancellationToken) => HandleInvokeAsync(input, MethodKind.Query, cancellationToken),
            Annotations = McpToolAnnotations.ReadOnly
        };

        if (!_isReadOnly)
        {
            yield return new McpToolInfo
            {
                Name = InvokeOperationToolName,
                Description = InvokeOperationDescription,
                InputSchema = InvokeMethodSchema,
                Handler = (input, cancellationToken) => HandleInvokeAsync(input, MethodKind.Operation, cancellationToken),
                Annotations = McpToolAnnotations.Destructive
            };
        }
    }

    private Task<object?> HandleListMethodsAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var subject = ResolveSubject(input.GetProperty("path").GetString()!);
        if (subject is null)
        {
            return Task.FromResult<object?>(new { error = "Subject not found." });
        }

        var methods = subject.GetAllMethods().Select(method => new
        {
            name = method.PropertyName,
            kind = method.Kind.ToString().ToLowerInvariant(),
            title = method.Title,
            description = method.Description,
            returnType = JsonSchemaTypeMapper.ToJsonSchemaType(method.ResultType),
            requiresConfirmation = method.RequiresConfirmation,
            parameters = method.Parameters
                .Where(parameter => parameter.RequiresInput)
                .Select(parameter => McpMethodParameter.Create(
                    parameter.Name, parameter.Type, parameter.IsNullable, GetUnitDescription(parameter.Unit)))
                .ToArray()
        });

        return Task.FromResult<object?>(new { methods });
    }

    private async Task<object?> HandleInvokeAsync(JsonElement input, MethodKind allowedKind, CancellationToken cancellationToken)
    {
        var subject = ResolveSubject(input.GetProperty("path").GetString()!);
        if (subject is null)
        {
            return new { error = "Subject not found." };
        }

        var methodName = input.GetProperty("method").GetString()!;
        var methodProperty = subject.TryGetProperty(methodName);
        if (methodProperty?.GetValue() is not MethodMetadata method)
        {
            return new { error = $"Method not found: {methodName}" };
        }

        var callError = GetCallError(subject, methodName, method, allowedKind);
        if (callError is not null)
        {
            return new { error = callError };
        }

        var inputParameters = method.Parameters.Where(parameter => parameter.RequiresInput).ToArray();
        var (arguments, argumentError) = ReadArguments(input, inputParameters);
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
        MethodKind.Query => $"'{methodName}' is a query; call it with {InvokeQueryToolName}.",
        _ when _isReadOnly => $"'{methodName}' is an operation; it can only be called with {InvokeOperationToolName}, which is not available in read-only mode.",
        _ => $"'{methodName}' is an operation; call it with {InvokeOperationToolName}."
    };

    private static (object?[]? Arguments, string? Error) ReadArguments(JsonElement input, MethodParameter[] inputParameters)
    {
        var objectError = TryGetArgumentsObject(input, inputParameters, out var argumentsObject);
        if (objectError is not null)
        {
            return (null, objectError);
        }

        var arguments = new object?[inputParameters.Length];
        for (var i = 0; i < inputParameters.Length; i++)
        {
            var parameter = inputParameters[i];
            if (argumentsObject is not { } providedArguments || !providedArguments.TryGetProperty(parameter.Name, out var argumentValue))
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

    private static string? TryGetArgumentsObject(JsonElement input, MethodParameter[] inputParameters, out JsonElement? argumentsObject)
    {
        argumentsObject = null;
        if (!input.TryGetProperty("parameters", out var argumentsElement) || argumentsElement.ValueKind == JsonValueKind.Null)
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

        argumentsObject = argumentsElement;
        return null;
    }

    private static string DescribeExpectedParameters(MethodParameter[] inputParameters) =>
        inputParameters.Length == 0
            ? "The method takes no parameters."
            : $"Expected parameters: {string.Join(", ", inputParameters.Select(parameter => parameter.Name))}.";

    private static string? GetUnitDescription(StateUnit? unit) => unit switch
    {
        null or StateUnit.Default => null,
        StateUnit.Percent => "fraction where 1 = 100% (0.2 = 20%)",
        _ => $"unit: {unit}"
    };

    private RegisteredSubject? ResolveSubject(string path) =>
        McpSubjectPathResolver.TryResolveSubject(_rootSubjectProvider, _pathProvider, path);
}
