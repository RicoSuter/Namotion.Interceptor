using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Turns the blocks and expressions of a markdown page into the child subjects of its <see cref="MarkdownFile"/>.
/// </summary>
public sealed partial class MarkdownContentParser
{
    private const string HtmlKeyPrefix = "Html_";
    private const string ExpressionKeyPrefix = "Expression_";
    private const string TypeDiscriminator = "$type";

    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> ConfigurationPropertyCache = new();

    // A JsonObject with a duplicate key parses and then throws on the first write, so the duplicate is rejected while parsing.
    private static readonly JsonDocumentOptions StrictJsonOptions = new() { AllowDuplicateProperties = false };

    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly SubjectPathResolver _pathResolver;
    private readonly TypeProvider _typeProvider;

    // The top-level keys of the configuration last applied to a subject. See CanUpdateInPlace.
    private readonly ConditionalWeakTable<IInterceptorSubject, string[]> _appliedConfigurationKeys = new();

    /// <summary>
    /// Rewrites relative .md links to /pages/... route format with Children segments.
    /// </summary>
    private static string RewriteLink(string url, string basePath)
    {
        // Skip absolute URLs and anchors
        if (string.IsNullOrEmpty(url) ||
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("#") ||
            url.StartsWith("/"))
        {
            return url;
        }

        // Only process .md links
        if (!url.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        // Resolve relative path against base path
        var resolvedPath = ResolvePath(basePath, url);

        // Convert to /pages/... format with Children segments
        // e.g., "foo/bar/baz.md" => "/pages/foo/bar/baz.md"
        var segments = resolvedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder("/pages");

        foreach (var segment in segments)
        {
            result.Append("/");
            result.Append(segment);
        }

        return result.ToString();
    }

    /// <summary>
    /// Computes the base path used to resolve relative links in markdown.
    /// During initial scan, markdown files are parsed BEFORE being placed in their parent's Children,
    /// so their own graph path isn't available yet. Use the storage container's graph path (which IS
    /// attached by the time ScanAsync runs) combined with the file's storage-local directory.
    /// </summary>
    private string GetLinkBasePath(MarkdownFile parent)
    {
        var fileDirectory = Path.GetDirectoryName(parent.FullPath)?.Replace('\\', '/') ?? string.Empty;

        if (parent.Storage is not IInterceptorSubject storageSubject
            || _pathResolver.GetPath(storageSubject, PathStyle.Route) is not { } storageGraphPath)
        {
            return fileDirectory;
        }

        var trimmedStorage = storageGraphPath.TrimEnd('/');
        var trimmedDirectory = fileDirectory.Trim('/');
        return string.IsNullOrEmpty(trimmedDirectory)
            ? trimmedStorage
            : $"{trimmedStorage}/{trimmedDirectory}";
    }

    /// <summary>
    /// Resolves a relative path against a base path, handling .. navigation.
    /// </summary>
    private static string ResolvePath(string basePath, string relativePath)
    {
        // Normalize to forward slashes
        basePath = basePath.Replace('\\', '/').TrimStart('/');
        relativePath = relativePath.Replace('\\', '/');

        // Remove ./ prefix
        if (relativePath.StartsWith("./"))
        {
            relativePath = relativePath[2..];
        }

        // Split base path into segments
        var baseSegments = basePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Process relative path
        var relativeSegments = relativePath.Split('/');
        foreach (var segment in relativeSegments)
        {
            if (segment == "..")
            {
                if (baseSegments.Count > 0)
                {
                    baseSegments.RemoveAt(baseSegments.Count - 1);
                }
            }
            else if (segment != "." && !string.IsNullOrEmpty(segment))
            {
                baseSegments.Add(segment);
            }
        }

        return string.Join("/", baseSegments);
    }

    public MarkdownContentParser(
        ConfigurableSubjectSerializer serializer,
        SubjectPathResolver pathResolver,
        TypeProvider typeProvider)
    {
        _serializer = serializer;
        _pathResolver = pathResolver;
        _typeProvider = typeProvider;
    }

    // Shared with the segment builder so that rendering the raw content and splitting the HTML
    // agree on what a {{...}} is.
    [GeneratedRegex(MarkdownSegmentBuilder.ExpressionPattern)]
    private static partial Regex ExpressionRegex();

    /// <summary>
    /// Renders markdown content by replacing {{ path }} expressions with their resolved values.
    /// Embedded subject blocks are kept as-is. Frontmatter is preserved.
    /// Uses the same resolution semantics as <see cref="RenderExpression"/> (relative to the markdown file).
    /// </summary>
    public string? RenderContent(string? content, MarkdownFile parent)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        return ExpressionRegex().Replace(content, match =>
        {
            var path = match.Groups[1].Value;
            try
            {
                var value = _pathResolver.ResolveValue(path, PathStyle.Canonical, relativeTo: parent);
                return value?.ToString() ?? string.Empty;
            }
            catch
            {
                return match.Value;
            }
        });
    }

    /// <summary>
    /// Parses page content and reconciles it with the existing children.
    /// </summary>
    public async Task<MarkdownParseResult> ParseAsync(
        string? content,
        MarkdownFile parent,
        IDictionary<string, IInterceptorSubject> existingChildren,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(content))
        {
            return MarkdownParseResult.Empty;
        }

        var page = MarkdownPageParser.Parse(content);
        var basePath = GetLinkBasePath(parent);
        var segments = MarkdownSegmentBuilder.Build(page, url => RewriteLink(url, basePath));

        var state = new ParseState(parent);
        var children = await BuildChildrenAsync(segments, existingChildren, state, cancellationToken);
        return new MarkdownParseResult(children, page, state.BlockSubjects, state.NamedSubjects);
    }

    private async Task<IDictionary<string, IInterceptorSubject>> BuildChildrenAsync(
        IReadOnlyList<MarkdownSegment> segments,
        IDictionary<string, IInterceptorSubject> existingChildren,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var children = new Dictionary<string, IInterceptorSubject>();

        for (var index = 0; index < segments.Count; index++)
        {
            switch (segments[index])
            {
                case HtmlMarkdownSegment html:
                    AddHtml(children, existingChildren, index, html.Html);
                    break;

                case ExpressionMarkdownSegment expression:
                    var expressionKey = $"{ExpressionKeyPrefix}{index}_{ComputeHash(expression.Path)}";
                    children[expressionKey] =
                        existingChildren.TryGetValue(expressionKey, out var existingExpression) && existingExpression is RenderExpression
                            ? existingExpression
                            : new RenderExpression(expression.Path, state.Page, _pathResolver);
                    break;

                case BlockMarkdownSegment block:
                    var (key, subject, error) = await CreateWidgetAsync(block, children, existingChildren, index, state, cancellationToken);
                    if (subject is null)
                    {
                        AddHtml(children, existingChildren, index, MarkdownErrorBox.Render(block.Block.Line, error!));
                    }
                    else
                    {
                        children[key!] = subject;
                    }

                    break;
            }
        }

        return children;
    }

    private static void AddHtml(
        Dictionary<string, IInterceptorSubject> children,
        IDictionary<string, IInterceptorSubject> existingChildren,
        int index,
        string html)
    {
        // The index is part of the key because the same HTML can occur several times.
        var key = $"{HtmlKeyPrefix}{index}_{ComputeHash(html)}";
        children[key] = existingChildren.TryGetValue(key, out var existing) && existing is HtmlSegment
            ? existing
            : new HtmlSegment(html);
    }

    private async Task<(string? Key, IInterceptorSubject? Subject, string? Error)> CreateWidgetAsync(
        BlockMarkdownSegment segment,
        Dictionary<string, IInterceptorSubject> children,
        IDictionary<string, IInterceptorSubject> existingChildren,
        int index,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var block = segment.Block;
        var head = block.Head!;

        // Claimed before every other check, so that a block that ends as an error box still owns its name.
        if (head.Id is not null && !state.ClaimedIds.Add(head.Id))
        {
            return (null, null, FormatUsedTwice(head.Id));
        }

        if (block.Kind == MarkdownBlockKind.Slot)
        {
            return (null, null, $"'{head.Name}' is not a slot at this position. A slot block must be directly inside the widget that declares it.");
        }

        var type = _typeProvider.Types.FirstOrDefault(candidate => candidate.FullName == head.Name);
        if (type is null || !typeof(IConfigurable).IsAssignableFrom(type))
        {
            return (null, null, $"Unknown type '{head.Name}'. Use the full type name including the namespace.");
        }

        if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
        {
            return (null, null, $"The type '{head.Name}' cannot be created because it is abstract, an interface or an open generic type.");
        }

        // An id can equal the positional key of another block, for example #Card_3.
        var key = head.Id ?? $"{type.Name}_{index}";
        if (children.ContainsKey(key))
        {
            return (null, null, FormatUsedTwice(key));
        }

        try
        {
            if (!TryBuildConfiguration(type, block, out var json, out var configurationError))
            {
                return (null, null, configurationError);
            }

            existingChildren.TryGetValue(key, out var existing);
            var subject = await CreateOrUpdateAsync(type, existing, json, cancellationToken);
            if (subject is null)
            {
                return (null, null, $"The type '{head.Name}' could not be created.");
            }

            var contentError = await ApplyContentAsync(subject, type, segment.Content, state, cancellationToken);
            if (contentError is not null)
            {
                return (null, null, contentError);
            }

            state.Register(block, head.Id, subject);
            return (key, subject, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Constructors and ApplyConfigurationAsync run foreign code. One failing block must not take the page down.
            return (null, null, $"The type '{head.Name}' could not be created: {exception.Message}");
        }
    }

    private static string FormatUsedTwice(string name) => $"The name '{name}' is used twice on this page.";

    private async Task<IInterceptorSubject?> CreateOrUpdateAsync(
        Type type, IInterceptorSubject? existing, string json, CancellationToken cancellationToken)
    {
        var keys = GetConfigurationKeys(json);

        IInterceptorSubject? subject;
        if (CanUpdateInPlace(existing, type, keys))
        {
            _serializer.UpdateConfiguration(existing, json);
            if (existing is IConfigurable configurable)
            {
                await configurable.ApplyConfigurationAsync(cancellationToken);
            }

            subject = existing;
        }
        else
        {
            subject = _serializer.Deserialize(json) as IInterceptorSubject;
        }

        if (subject is not null)
        {
            _appliedConfigurationKeys.AddOrUpdate(subject, keys);
        }

        return subject;
    }

    /// <summary>
    /// Records the configuration that was written to the page for a subject, so that the reload
    /// which follows the write updates the subject in place.
    /// </summary>
    public void RememberConfiguration(IInterceptorSubject subject, string configurationJson) =>
        _appliedConfigurationKeys.AddOrUpdate(subject, GetConfigurationKeys(configurationJson));

    // Updating in place only sets the keys that are present. A key that was removed from the page
    // would keep its old value on the instance, unlike after a restart, so the subject is recreated then.
    private bool CanUpdateInPlace([NotNullWhen(true)] IInterceptorSubject? existing, Type type, string[] keys)
    {
        if (existing is null || existing.GetType() != type)
        {
            return false;
        }

        return !_appliedConfigurationKeys.TryGetValue(existing, out var previousKeys)
            || Array.TrueForAll(previousKeys, previousKey => Array.IndexOf(keys, previousKey) >= 0);
    }

    private static string[] GetConfigurationKeys(string json)
    {
        using var document = JsonDocument.Parse(json);

        var keys = new List<string>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name != TypeDiscriminator)
            {
                keys.Add(property.Name);
            }
        }

        return keys.ToArray();
    }

    // Replaced in the slots task. Until then a widget block cannot have content.
    private Task<string?> ApplyContentAsync(
        IInterceptorSubject subject,
        Type type,
        IReadOnlyList<MarkdownSegment> content,
        ParseState state,
        CancellationToken cancellationToken)
    {
        var hasContent = content.Any(segment => segment is not HtmlMarkdownSegment html || !string.IsNullOrWhiteSpace(html.Html));
        return Task.FromResult(hasContent ? $"{type.Name} cannot have content." : null);
    }

    private static string ComputeHash(string content)
    {
        var byteCount = Encoding.UTF8.GetByteCount(content);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var bytesWritten = Encoding.UTF8.GetBytes(content, buffer);
            Span<byte> hashBuffer = stackalloc byte[32];
            SHA256.HashData(buffer.AsSpan(0, bytesWritten), hashBuffer);
            return Convert.ToHexStringLower(hashBuffer[..8]);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool TryBuildConfiguration(Type type, MarkdownBlock block, out string json, out string? error)
    {
        json = string.Empty;

        var properties = GetConfigurationProperties(type);
        var configuration = ParseConfigurationFence(type, block.ConfigurationJson, properties, out error);
        if (configuration is null)
        {
            return false;
        }

        foreach (var argument in block.Head!.Arguments)
        {
            if (!properties.TryGetValue(argument.Key, out var propertyType))
            {
                error = FormatUnknownProperty(type, argument.Key, properties);
                return false;
            }

            var node = CreateArgumentNode(argument.Value, propertyType);
            if (node is null || !_serializer.CanDeserialize(node.ToJsonString(), propertyType))
            {
                var expectedType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
                error = $"'{argument.Value}' is not a valid value for '{argument.Key}'. Expected a value of type {expectedType.Name}.";
                return false;
            }

            configuration[argument.Key] = node;
        }

        configuration[TypeDiscriminator] = type.FullName;
        json = configuration.ToJsonString();
        return true;
    }

    private static JsonObject? ParseConfigurationFence(
        Type type, string? text, IReadOnlyDictionary<string, Type> properties, out string? error)
    {
        error = null;
        if (text is null)
        {
            return new JsonObject();
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text, documentOptions: StrictJsonOptions);
        }
        catch (JsonException exception)
        {
            error = $"The json block must contain a valid JSON object without duplicate keys. {exception.Message}";
            return null;
        }

        if (node is not JsonObject configuration)
        {
            error = "The json block must contain a JSON object.";
            return null;
        }

        foreach (var (key, _) in configuration)
        {
            if (key != TypeDiscriminator && !properties.ContainsKey(key))
            {
                error = FormatUnknownProperty(type, key, properties);
                return null;
            }
        }

        return configuration;
    }

    private static string FormatUnknownProperty(Type type, string key, IReadOnlyDictionary<string, Type> properties) =>
        $"{type.Name} has no configuration property '{key}'. Available: {string.Join(", ", properties.Keys.Order())}.";

    private static IReadOnlyDictionary<string, Type> GetConfigurationProperties(Type type) =>
        ConfigurationPropertyCache.GetOrAdd(type, static key => key
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ConfigurationAttribute>(true) is not null)
            .ToDictionary(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name), property => property.PropertyType));

    // The literal an argument stands for: a bare literal for a bool or numeric property, a JSON string for every
    // other type. Null when the text is no such literal.
    private static JsonNode? CreateArgumentNode(string value, Type propertyType)
    {
        var target = Nullable.GetUnderlyingType(propertyType) ?? propertyType;

        if (target == typeof(bool))
        {
            return bool.TryParse(value, out var boolean) ? JsonValue.Create(boolean) : null;
        }

        var isNumeric = target == typeof(decimal) || (target.IsPrimitive && target != typeof(char));
        if (!isNumeric)
        {
            return JsonValue.Create(value);
        }

        try
        {
            var node = JsonNode.Parse(value);
            return node?.GetValueKind() == JsonValueKind.Number ? node : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class ParseState(MarkdownFile page)
    {
        public MarkdownFile Page { get; } = page;

        public Dictionary<int, IInterceptorSubject> BlockSubjects { get; } = new();

        public Dictionary<string, IInterceptorSubject> NamedSubjects { get; } = new();

        public HashSet<string> ClaimedIds { get; } = [];

        public void Register(MarkdownBlock block, string? id, IInterceptorSubject subject)
        {
            BlockSubjects[block.Index] = subject;
            if (id is not null)
            {
                NamedSubjects[id] = subject;
            }
        }
    }
}
