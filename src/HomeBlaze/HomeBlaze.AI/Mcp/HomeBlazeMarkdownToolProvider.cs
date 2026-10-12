using System.Text.Json;
using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Provides edit_markdown, write_markdown and create_markdown, which call the operations ReplaceText and ReplaceContent of
/// a Markdown file and CreateMarkdownFile of a local storage folder through the same path as invoke_operation, so its
/// argument checks, operation filter and confirmation rule apply. The tools are only offered when the configuration is
/// not read-only.
/// </summary>
public class HomeBlazeMarkdownToolProvider : IMcpToolProvider
{
    /// <summary>The name of the tool that replaces one passage of a Markdown file.</summary>
    public const string EditMarkdownToolName = "edit_markdown";

    /// <summary>The name of the tool that replaces the whole source of a Markdown file.</summary>
    public const string WriteMarkdownToolName = "write_markdown";

    /// <summary>The name of the tool that creates a Markdown file in a folder.</summary>
    public const string CreateMarkdownToolName = "create_markdown";

    /// <summary>The names of the tools, all of which change pages and documents.</summary>
    public static IReadOnlyList<string> ToolNames { get; } = [EditMarkdownToolName, WriteMarkdownToolName, CreateMarkdownToolName];

    private const string BlocksRule =
        "Blocks, which start with a line of two or more colons (::) and create widgets, layouts, slots, subjects and " +
        "styled sections, must stay exactly as they are and in the same order, including their content up to their " +
        "closing line; text directly after a block without a closing line belongs to that block.";

    private static readonly string MaxContentSizeText = $"at most {MarkdownFile.MaxContentSize / 1024} KB";

    private static readonly string EditMarkdownDescription =
        "Replace one passage of a Markdown page or document (*.md): old_text must occur exactly once in the Markdown source, " +
        $"as returned by {HomeBlazeMcpToolProvider.InvokeQueryToolName} method ReadRawContent (with {{{{ path }}}} expressions unevaluated), and is replaced by new_text. " +
        $"Prefer this over {WriteMarkdownToolName} for small changes. Only Markdown files in local storage can be changed. " +
        $"{BlocksRule} {MarkdownFile.ActiveContent} cannot be added. " +
        HomeBlazeMcpInstructions.PageChangeCaution;

    private static readonly string WriteMarkdownDescription =
        $"Replace the whole Markdown source of a Markdown page or document (*.md) with content ({MaxContentSizeText}). Read the " +
        $"source first with {HomeBlazeMcpToolProvider.InvokeQueryToolName} method ReadRawContent and keep what should stay; for small changes use {EditMarkdownToolName}. " +
        "Only Markdown files in local storage can be changed. " +
        $"{BlocksRule} {MarkdownFile.ActiveContent} cannot be added. " +
        HomeBlazeMcpInstructions.PageChangeCaution;

    private static readonly string CreateMarkdownDescription =
        "Create a Markdown page or document: folder_path is the path of a local storage folder or storage, name a plain " +
        $"file name ending in .md that does not exist there yet, content the Markdown source ({MaxContentSizeText}) without " +
        $"blocks (lines that start with two or more colons), {MarkdownFile.ActiveContent}. " +
        HomeBlazeMcpInstructions.PageChangeCaution;

    private static readonly MarkdownTool EditMarkdownTool = new(
        EditMarkdownToolName,
        EditMarkdownDescription,
        CreateSchema(
            ("path", "Path of the Markdown file"),
            ("old_text", "The exact passage of the Markdown source to replace; must occur exactly once"),
            ("new_text", "The text that replaces the passage")),
        "path",
        "ReplaceText",
        [("old_text", "oldText"), ("new_text", "newText")],
        GetMarkdownFileError);

    private static readonly MarkdownTool WriteMarkdownTool = new(
        WriteMarkdownToolName,
        WriteMarkdownDescription,
        CreateSchema(
            ("path", "Path of the Markdown file"),
            ("content", "The new Markdown source of the whole file")),
        "path",
        "ReplaceContent",
        [("content", "content")],
        GetMarkdownFileError);

    private static readonly MarkdownTool CreateMarkdownTool = new(
        CreateMarkdownToolName,
        CreateMarkdownDescription,
        CreateSchema(
            ("folder_path", "Path of the folder to create the file in"),
            ("name", "File name ending in .md, without folders"),
            ("content", "The Markdown source of the new file")),
        "folder_path",
        "CreateMarkdownFile",
        [("name", "name"), ("content", "content")],
        GetFolderError);

    private static readonly MarkdownTool[] Tools = [EditMarkdownTool, WriteMarkdownTool, CreateMarkdownTool];

    private readonly bool _isReadOnly;
    private readonly McpMethodInvoker _invoker;

    /// <param name="rootSubjectProvider">Resolves the root subject when a tool runs.</param>
    /// <param name="pathProvider">Resolves subject paths.</param>
    /// <param name="serviceProvider">Services for [FromServices] method parameters.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="isReadOnly">Whether the tools are left out.</param>
    /// <param name="operationFilter">Decides whether a tool may call an operation of a subject, as for invoke_operation:
    /// returns null to allow it, or the error message for the client. Null allows all.</param>
    public HomeBlazeMarkdownToolProvider(
        Func<IInterceptorSubject> rootSubjectProvider,
        PathProviderBase pathProvider,
        IServiceProvider serviceProvider,
        ILogger<HomeBlazeMarkdownToolProvider> logger,
        bool isReadOnly,
        Func<RegisteredSubject, MethodMetadata, string?>? operationFilter = null)
    {
        _isReadOnly = isReadOnly;
        _invoker = new McpMethodInvoker(rootSubjectProvider, pathProvider, serviceProvider, logger, isReadOnly, operationFilter);
    }

    public IEnumerable<McpToolInfo> GetTools()
    {
        if (_isReadOnly)
        {
            yield break;
        }

        foreach (var tool in Tools)
        {
            yield return new McpToolInfo
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = tool.Schema,
                Handler = (input, cancellationToken) => HandleAsync(input, tool, cancellationToken),
                Annotations = McpToolAnnotations.Destructive
            };
        }
    }

    private Task<object?> HandleAsync(JsonElement input, MarkdownTool tool, CancellationToken cancellationToken)
    {
        if (!input.TryGetProperty(tool.PathArgument, out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
        {
            return Task.FromResult<object?>(new { error = $"Missing parameter '{tool.PathArgument}'." });
        }

        var path = pathElement.GetString()!;
        var subject = _invoker.TryResolveSubject(path);
        if (subject is null)
        {
            return Task.FromResult<object?>(new { error = "Subject not found." });
        }

        if (tool.GetTargetError(subject.Subject, path) is { } targetError)
        {
            return Task.FromResult<object?>(new { error = targetError });
        }

        var arguments = new Dictionary<string, JsonElement>(tool.Arguments.Length);
        foreach (var (argumentName, parameterName) in tool.Arguments)
        {
            if (!input.TryGetProperty(argumentName, out var argument))
            {
                return Task.FromResult<object?>(new { error = $"Missing parameter '{argumentName}'." });
            }

            arguments[parameterName] = argument;
        }

        return _invoker.InvokeAsync(subject, tool.MethodName, MethodKind.Operation, JsonSerializer.SerializeToElement(arguments), cancellationToken);
    }

    private static string? GetMarkdownFileError(IInterceptorSubject subject, string path) => subject switch
    {
        MarkdownFile { IsLocal: true } => null,
        MarkdownFile => $"'{path}' is not in local storage; only local Markdown files can be changed.",
        _ => $"'{path}' is not a Markdown file."
    };

    private static string? GetFolderError(IInterceptorSubject subject, string path) =>
        subject is VirtualFolder { IsLocal: true } or FluentStorageContainer { IsLocal: true }
            ? null
            : $"'{path}' is not a local storage folder.";

    private static JsonElement CreateSchema(params (string Name, string Description)[] properties) =>
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = properties.ToDictionary(property => property.Name, property => new { type = "string", description = property.Description }),
            required = properties.Select(property => property.Name).ToArray()
        });

    // Every argument in Schema is a required string. PathArgument holds the path of the target subject, Arguments maps
    // the other arguments to the operation parameters they are passed as, and GetTargetError returns the error for a
    // target the tool does not apply to, or null.
    private sealed record MarkdownTool(
        string Name,
        string Description,
        JsonElement Schema,
        string PathArgument,
        string MethodName,
        (string Argument, string Parameter)[] Arguments,
        Func<IInterceptorSubject, string, string?> GetTargetError);
}
