using System.Text.Json;
using System.Text.Json.Nodes;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Pages;

/// <summary>
/// Replaces the plaintext secrets in the block configurations of a page, head arguments and JSON fences, by references to
/// new keys of the secret store.
/// </summary>
internal sealed class SecretPageConverter
{
    private readonly ConfigurableSubjectSerializer _serializer;
    private readonly TypeProvider _typeProvider;

    public SecretPageConverter(ConfigurableSubjectSerializer serializer, TypeProvider typeProvider)
    {
        _serializer = serializer;
        _typeProvider = typeProvider;
    }

    /// <summary>
    /// Converts the source of a page. Returns false, with <paramref name="converted"/> being <paramref name="source"/>,
    /// when no block holds a plaintext secret or the source cannot be parsed. Text outside the changed head lines and
    /// fences stays as it is.
    /// </summary>
    /// <exception cref="IOException">The secret store cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The secret store cannot be written.</exception>
    /// <exception cref="InvalidOperationException">A block type has a secret configuration property that is not a string
    /// property of a subject.</exception>
    public bool TryConvert(string source, out string converted)
    {
        converted = source;
        if (!source.Contains("::", StringComparison.Ordinal))
        {
            return false;
        }

        MarkdownPage page;
        try
        {
            page = MarkdownPageParser.Parse(source);
        }
        catch (ArgumentException)
        {
            // Markdown nested beyond the depth limit of Markdig, which no page reads, so the source is written as it is.
            return false;
        }

        List<(MarkdownSpan Span, string Text)>? edits = null;
        foreach (var block in page.Blocks)
        {
            Visit(source, block, ownerType: null, ref edits);
        }

        if (edits is null)
        {
            return false;
        }

        // From the end, so that the spans of the earlier edits stay valid.
        edits.Sort((left, right) => right.Span.Start.CompareTo(left.Span.Start));
        var result = source;
        foreach (var (span, text) in edits)
        {
            result = result.Remove(span.Start, span.Length).Insert(span.Start, text);
        }

        converted = result;
        return true;
    }

    private void Visit(string source, MarkdownBlock block, Type? ownerType, ref List<(MarkdownSpan Span, string Text)>? edits)
    {
        var type = ResolveType(block, ownerType);
        if (type is not null && block.Head is { } head)
        {
            ConvertHead(block, head, type, ref edits);
            ConvertFence(source, block, type, ref edits);
        }

        foreach (var child in block.Children)
        {
            Visit(source, child, type, ref edits);
        }
    }

    private Type? ResolveType(MarkdownBlock block, Type? ownerType)
    {
        if (block.Head is not { } head)
        {
            return null;
        }

        if (block.Kind == MarkdownBlockKind.Widget)
        {
            foreach (var type in _typeProvider.Types)
            {
                if (type.FullName == head.Name)
                {
                    return type;
                }
            }

            return null;
        }

        // A slot block configures a value of the slot of its owner; an open slot takes content, not configuration.
        if (block.Kind == MarkdownBlockKind.Slot && ownerType is not null &&
            SlotMap.For(ownerType).Named.TryGetValue(head.Name, out var slot) && !slot.IsOpen)
        {
            return slot.ValueType;
        }

        return null;
    }

    private void ConvertHead(MarkdownBlock block, MarkdownBlockHead head, Type type, ref List<(MarkdownSpan Span, string Text)>? edits)
    {
        if (head.Arguments.Count == 0)
        {
            return;
        }

        var arguments = new JsonObject();
        foreach (var argument in head.Arguments)
        {
            arguments[argument.Key] = argument.Value;
        }

        if (!_serializer.TryConvertSecrets(arguments.ToJsonString(), type, ConfigurationNaming.CSharpProperty, out var converted))
        {
            return;
        }

        var convertedArguments = JsonNode.Parse(converted)!.AsObject();
        var newArguments = new List<MarkdownBlockArgument>(head.Arguments.Count);
        foreach (var argument in head.Arguments)
        {
            var value = convertedArguments[argument.Key]?.GetValue<string>() ?? argument.Value;
            newArguments.Add(value == argument.Value ? argument : new MarkdownBlockArgument(argument.Key, value, false));
        }

        (edits ??= []).Add((block.HeadSpan, (head with { Arguments = newArguments }).Format()));
    }

    private void ConvertFence(string source, MarkdownBlock block, Type type, ref List<(MarkdownSpan Span, string Text)>? edits)
    {
        if (block.ConfigurationJson is not { } json || block.ConfigurationSpan is not { } span)
        {
            return;
        }

        try
        {
            // Strict, as the parser reads it: a fence with a duplicate key is an error block and is left alone.
            using var _ = JsonDocument.Parse(json, MarkdownContentParser.StrictJsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (!_serializer.TryConvertSecrets(json, type, ConfigurationNaming.CSharpProperty, out var converted))
        {
            return;
        }

        var lineEnding = MarkdownBlockWriter.GetLineEnding(source, block.HeadSpan.Start + block.HeadSpan.Length);
        (edits ??= []).Add((span, MarkdownBlockWriter.FormatFence(JsonNode.Parse(converted)!.AsObject(), lineEnding)));
    }
}
