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

    private const string ListMethodsToolName = "list_methods";

    private const string ListMethodsDescription =
        "List the methods of a subject, with each parameter's type, format and unit. Every method is either a query " +
        $"(reads, no side effects; call it with {InvokeQueryToolName}) or an operation (changes devices or sends something; " +
        $"call it with {InvokeOperationToolName}). The kind field of each method says which. Operations with requiresConfirmation " +
        "set need a person's confirmation and cannot be called through MCP.";

    private const string ReadOnlyListMethodsDescription =
        "List the methods of a subject, with each parameter's type, format and unit. Every method is either a query " +
        $"(reads, no side effects; call it with {InvokeQueryToolName}) or an operation (changes devices or sends something). " +
        "The kind field of each method says which; operations are listed for information but cannot be called on this server.";

    private const string InvokeQueryDescription =
        "Call a query method of a subject: it reads and has no side effects, for example ReadEvaluatedContent on a " +
        "Markdown page or document (*.md) to read it, with includeSourcePaths=true to see where each value comes from. " +
        $"Find methods and their kind with {ListMethodsToolName}.";

    private const string InvokeOperationDescription =
        "Call an operation method of a subject: it can change devices or send something, for example SendNotification. " +
        $"Operations that require confirmation by a person cannot be called. Find methods and their kind with {ListMethodsToolName}. " +
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
            parameters = new { type = "object", description = $"Arguments by parameter name, in the type, format and unit {ListMethodsToolName} gives (optional)" }
        },
        required = new[] { "path", "method" }
    });

    private readonly bool _isReadOnly;
    private readonly McpMethodInvoker _invoker;

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
        _isReadOnly = isReadOnly;
        _invoker = new McpMethodInvoker(rootSubjectProvider, pathProvider, serviceProvider, logger, isReadOnly, operationFilter);
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        yield return new McpToolInfo
        {
            Name = ListMethodsToolName,
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
        var subject = _invoker.TryResolveSubject(input.GetProperty("path").GetString()!);
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

    private Task<object?> HandleInvokeAsync(JsonElement input, MethodKind allowedKind, CancellationToken cancellationToken)
    {
        var subject = _invoker.TryResolveSubject(input.GetProperty("path").GetString()!);
        if (subject is null)
        {
            return Task.FromResult<object?>(new { error = "Subject not found." });
        }

        var arguments = input.TryGetProperty("parameters", out var argumentsElement) ? argumentsElement : default;
        return _invoker.InvokeAsync(subject, input.GetProperty("method").GetString()!, allowedKind, arguments, cancellationToken);
    }

    private static string? GetUnitDescription(StateUnit? unit) => unit switch
    {
        null or StateUnit.Default => null,
        StateUnit.Percent => "fraction where 1 = 100% (0.2 = 20%)",
        _ => $"unit: {unit}"
    };
}
