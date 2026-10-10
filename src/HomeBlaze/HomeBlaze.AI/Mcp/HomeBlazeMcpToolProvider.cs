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
/// Provides the list_methods and invoke_method tools for HomeBlaze subjects.
/// </summary>
public class HomeBlazeMcpToolProvider : IMcpToolProvider
{
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

    public HomeBlazeMcpToolProvider(
        Func<IInterceptorSubject> rootSubjectProvider,
        PathProviderBase pathProvider,
        IServiceProvider serviceProvider,
        ILogger<HomeBlazeMcpToolProvider> logger,
        bool isReadOnly)
    {
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
            Description = "List operations and queries available on a subject at the given path, with each parameter's type, format and unit.",
            InputSchema = ListMethodsSchema,
            Handler = HandleListMethodsAsync
        };

        yield return new McpToolInfo
        {
            Name = "invoke_method",
            Description = "Execute a method on a subject. When server is read-only, only query methods are allowed.",
            InputSchema = InvokeMethodSchema,
            Handler = HandleInvokeMethodAsync
        };
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
            parameters = method.Parameters
                .Where(parameter => parameter.RequiresInput)
                .Select(parameter => McpMethodParameter.Create(
                    parameter.Name, parameter.Type, parameter.IsNullable, GetUnitDescription(parameter.Unit)))
                .ToArray()
        });

        return Task.FromResult<object?>(new { methods });
    }

    private async Task<object?> HandleInvokeMethodAsync(JsonElement input, CancellationToken cancellationToken)
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

        if (_isReadOnly && method.Kind != MethodKind.Query)
        {
            return new { error = "Operations are not allowed in read-only mode." };
        }

        var inputParameters = method.Parameters.Where(parameter => parameter.RequiresInput).ToArray();
        var (arguments, argumentError) = ReadArguments(input, inputParameters);
        if (argumentError is not null)
        {
            return new { error = argumentError };
        }

        try
        {
            var result = await method.InvokeAsync(arguments, _serviceProvider, cancellationToken);
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

    private static (object?[]? Arguments, string? Error) ReadArguments(JsonElement input, MethodParameter[] inputParameters)
    {
        JsonElement? argumentsObject = null;
        if (input.TryGetProperty("parameters", out var argumentsElement) && argumentsElement.ValueKind != JsonValueKind.Null)
        {
            if (argumentsElement.ValueKind != JsonValueKind.Object)
            {
                return (null, $"'parameters' must be an object. {DescribeExpectedParameters(inputParameters)}");
            }

            foreach (var argument in argumentsElement.EnumerateObject())
            {
                if (!inputParameters.Any(parameter => parameter.Name == argument.Name))
                {
                    return (null, $"Unknown parameter '{argument.Name}'. {DescribeExpectedParameters(inputParameters)}");
                }
            }

            argumentsObject = argumentsElement;
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

    private RegisteredSubject? ResolveSubject(string path)
    {
        var rootRegistered = _rootSubjectProvider().TryGetRegisteredSubject();
        if (rootRegistered is null)
        {
            return null;
        }

        var isRootPath = string.IsNullOrEmpty(path) || path == "/";

        if (isRootPath)
        {
            return rootRegistered;
        }

        return _pathProvider.TryGetSubjectFromPath(rootRegistered, path);
    }
}
