using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Services.Mcp;

/// <summary>
/// Provides list_methods and invoke_method tools for HomeBlaze subjects.
/// </summary>
public class HomeBlazeMcpToolProvider : IMcpToolProvider
{
    private readonly IInterceptorSubject _rootSubject;
    private readonly PathProviderBase _pathProvider;
    private readonly bool _isReadOnly;

    public HomeBlazeMcpToolProvider(
        IInterceptorSubject rootSubject,
        PathProviderBase pathProvider,
        bool isReadOnly)
    {
        _rootSubject = rootSubject;
        _pathProvider = pathProvider;
        _isReadOnly = isReadOnly;
    }

    public IEnumerable<McpToolDescriptor> GetTools()
    {
        yield return new McpToolDescriptor
        {
            Name = "list_methods",
            Description = "List operations and queries available on a subject at the given path.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { path = new { type = "string", description = "Subject path" } },
                required = new[] { "path" }
            }),
            Handler = HandleListMethodsAsync
        };

        yield return new McpToolDescriptor
        {
            Name = "invoke_method",
            Description = "Execute a method on a subject. When server is read-only, only query methods are allowed.",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Subject path" },
                    method = new { type = "string", description = "Method name" },
                    arguments = new { type = "object", description = "Method arguments (optional)" }
                },
                required = new[] { "path", "method" }
            }),
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
            parameters = method.Parameters
                .Where(parameter => parameter.RequiresInput)
                .Select(parameter => new { name = parameter.Name, type = parameter.Type.Name })
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

        try
        {
            // Parse user-input arguments from JSON
            object?[]? userParameters = null;
            if (input.TryGetProperty("arguments", out var argumentsElement))
            {
                var inputParams = method.Parameters.Where(parameter => parameter.RequiresInput).ToArray();
                userParameters = new object?[inputParams.Length];

                for (var i = 0; i < inputParams.Length; i++)
                {
                    var parameter = inputParams[i];
                    if (argumentsElement.TryGetProperty(parameter.Name, out var argumentValue))
                    {
                        userParameters[i] = JsonSerializer.Deserialize(argumentValue.GetRawText(), parameter.Type);
                    }
                }
            }

            var result = await method.InvokeAsync(userParameters, null, cancellationToken);
            return result is not null ? new { success = true, result } : (object)new { success = true };
        }
        catch (Exception exception)
        {
            return new { error = exception.Message };
        }
    }

    private RegisteredSubject? ResolveSubject(string path)
    {
        var rootRegistered = _rootSubject.TryGetRegisteredSubject();
        if (rootRegistered is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(path))
        {
            return rootRegistered;
        }

        var property = _pathProvider.TryGetPropertyFromPath(rootRegistered, path);
        var childSubject = property?.GetValue() as IInterceptorSubject;
        return childSubject?.TryGetRegisteredSubject();
    }
}
