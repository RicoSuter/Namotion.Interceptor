using System.Globalization;
using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.History;
using HomeBlaze.History.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Provides list_methods, invoke_method, and get_property_history tools for HomeBlaze subjects.
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
            parameters = new { type = "object", description = "Method parameters (optional)" }
        },
        required = new[] { "path", "method" }
    });

    private static readonly JsonElement GetPropertyHistorySchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            paths = new
            {
                type = "array",
                items = new { type = "string" },
                description = "One or more canonical property paths, e.g. /Devices/Sensor/Temperature."
            },
            from = new { type = "string", description = "Range start, ISO 8601. A bare timestamp is treated as UTC." },
            to = new { type = "string", description = "Range end, ISO 8601. Defaults to now." },
            bucket = new { type = "string", description = "Bucket size for downsampling, e.g. 5m, 30s, 1h, 7d. Omit for raw samples." },
            aggregation = new
            {
                type = "string",
                description = "Last, First, SampleAverage, TimeWeightedAverage, Minimum, Maximum, Sum, Count, " +
                              "or StandardDeviation. Case-insensitive. Defaults to Last."
            }
        },
        required = new[] { "paths", "from" }
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
            Description = "List operations and queries available on a subject at the given path.",
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

        yield return new McpToolInfo
        {
            Name = "get_property_history",
            Description = "Query recorded history for one or more [State] property paths over a time range. " +
                          "Supports raw samples or bucketed downsampling with an aggregation. Returns a per-path " +
                          "map with a value_type hint, the points (null entries are gaps), and a truncated flag. " +
                          "Use browse or search to discover paths first.",
            InputSchema = GetPropertyHistorySchema,
            Handler = HandleGetPropertyHistoryAsync
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
            returnType = Namotion.Interceptor.Mcp.Tools.JsonSchemaTypeMapper.ToJsonSchemaType(method.ResultType),
            parameters = method.Parameters
                .Where(parameter => parameter.RequiresInput)
                .Select(parameter => new
                {
                    name = parameter.Name,
                    type = Namotion.Interceptor.Mcp.Tools.JsonSchemaTypeMapper.ToJsonSchemaType(parameter.Type)
                })
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
            if (input.TryGetProperty("parameters", out var argumentsElement))
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

            var result = await method.InvokeAsync(userParameters, _serviceProvider, cancellationToken);
            return result is not null ? new { success = true, result } : new { success = true };
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to invoke method '{MethodName}' on subject.", methodName);
            return new { error = "Method invocation failed. Check server logs for details." };
        }
    }

    private async Task<object?> HandleGetPropertyHistoryAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty("paths", out var pathsElement) || pathsElement.ValueKind != JsonValueKind.Array)
        {
            return new { error = "Parameter 'paths' is required and must be an array of canonical property paths." };
        }

        var paths = pathsElement.EnumerateArray()
            .Select(element => element.GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();

        if (paths.Length == 0)
        {
            return new { error = "Parameter 'paths' must contain at least one path." };
        }

        if (!input.TryGetProperty("from", out var fromElement) || fromElement.ValueKind != JsonValueKind.String)
        {
            return new { error = "Parameter 'from' is required (ISO 8601 timestamp)." };
        }

        DateTimeOffset from;
        DateTimeOffset to;
        TimeSpan? bucket;
        try
        {
            from = HistoryToolParsing.ParseTimestamp(fromElement.GetString()!);
            to = input.TryGetProperty("to", out var toElement) && toElement.ValueKind == JsonValueKind.String
                ? HistoryToolParsing.ParseTimestamp(toElement.GetString()!)
                : DateTimeOffset.UtcNow;
            bucket = input.TryGetProperty("bucket", out var bucketElement) && bucketElement.ValueKind == JsonValueKind.String
                ? HistoryToolParsing.ParseBucket(bucketElement.GetString())
                : null;
        }
        catch (FormatException exception)
        {
            return new { error = $"Could not parse a time parameter: {exception.Message}" };
        }

        var aggregation = HistoryToolParsing.NormalizeAggregation(
            input.TryGetProperty("aggregation", out var aggregationElement) ? aggregationElement.GetString() : null);
        if (aggregation is null)
        {
            return new
            {
                error = "Unknown aggregation.",
                available = HistoryToolParsing.AllAggregations.OrderBy(name => name).ToArray()
            };
        }

        var rootSubject = _rootSubjectProvider();
        var registry = rootSubject.Context.GetService<ISubjectRegistry>();
        var rootRegistered = rootSubject.TryGetRegisteredSubject();
        var maxPoints = bucket is null ? 10_000 : 1_000;

        var valueTypes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var resolved = rootRegistered is null ? null : _pathProvider.TryGetPropertyFromPath(rootRegistered, path);
            valueTypes[path] = resolved is { } match ? HistoryToolParsing.ValueType(match.Property.Type) : null;
        }

        IReadOnlyList<HistorySeries> seriesList;
        try
        {
            seriesList = await registry
                .QueryHistoryAsync(paths, from, to, bucket, aggregation, maxPoints, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HistoryAggregationNotSupportedException exception)
        {
            return new
            {
                error = exception.Message,
                available = exception.Available.OrderBy(name => name).ToArray()
            };
        }

        var response = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var series in seriesList)
        {
            response[series.PropertyPath] = new
            {
                value_type = valueTypes.GetValueOrDefault(series.PropertyPath),
                truncated = series.Truncated,
                points = series.Points.Select(point => new
                {
                    t = point.Timestamp.ToString("o", CultureInfo.InvariantCulture),
                    value = point.Number is { } number ? (object?)number
                          : point.Json is { } json ? json
                          : null
                }).ToArray()
            };
        }

        return response;
    }

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
