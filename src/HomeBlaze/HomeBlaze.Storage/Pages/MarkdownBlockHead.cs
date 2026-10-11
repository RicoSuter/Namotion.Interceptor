using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace HomeBlaze.Storage.Pages;

/// <summary>One key=value argument on the head line of a block.</summary>
public sealed record MarkdownBlockArgument(string Key, string Value, bool IsQuoted);

/// <summary>
/// The parsed head line of a block: fence, name, optional #id and arguments. The name follows the fence
/// with or without spaces, and <see cref="Format"/> writes one space.
/// </summary>
public sealed partial record MarkdownBlockHead(
    int FenceLength,
    string Name,
    string? Id,
    IReadOnlyList<MarkdownBlockArgument> Arguments)
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex SlotNameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex IdRegex();

    /// <summary>A name with a dot is a type name, a name without one is a slot name.</summary>
    public bool IsWidget => Name.Contains('.');

    public static bool IsValidId(string id) => IdRegex().IsMatch(id);

    /// <summary>Gets whether a head line can have the name as the name of a slot block: a block name without a dot.</summary>
    internal static bool IsValidSlotName(string name) => SlotNameRegex().IsMatch(name);

    public static bool TryParse(
        string line,
        [NotNullWhen(true)] out MarkdownBlockHead? head,
        [NotNullWhen(false)] out string? error)
    {
        head = null;
        error = null;

        var text = line.Trim();
        var position = 0;
        while (position < text.Length && text[position] == MarkdownBlockFence.Character)
        {
            position++;
        }

        if (position < MarkdownBlockFence.MinimumLength)
        {
            error = $"A block starts with at least {MarkdownBlockFence.MinimumLength} '{MarkdownBlockFence.Character}' characters.";
            return false;
        }

        var fenceLength = position;
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        var nameStart = position;
        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '#')
        {
            position++;
        }

        var name = text[nameStart..position];
        if (!NameRegex().IsMatch(name))
        {
            error = $"'{name}' is not a valid block name.";
            return false;
        }

        string? id = null;
        if (position < text.Length && text[position] == '#')
        {
            position++;
            var idStart = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            id = text[idStart..position];
            if (!IdRegex().IsMatch(id))
            {
                error = $"'{id}' is not a valid name after '#'.";
                return false;
            }
        }

        var arguments = new List<MarkdownBlockArgument>();
        while (true)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }

            if (position >= text.Length)
            {
                break;
            }

            var keyStart = position;
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
            {
                position++;
            }

            var key = text[keyStart..position];
            if (key.Length == 0 || position >= text.Length || text[position] != '=')
            {
                error = $"Expected key=value but found '{text[keyStart..]}'.";
                return false;
            }

            position++;
            if (arguments.Exists(argument => argument.Key == key))
            {
                error = $"Argument '{key}' is set twice.";
                return false;
            }

            if (position < text.Length && text[position] == '"')
            {
                position++;
                var value = new StringBuilder();
                var isClosed = false;
                while (position < text.Length)
                {
                    var character = text[position++];
                    if (character == '\\' && position < text.Length && text[position] is '"' or '\\')
                    {
                        value.Append(text[position++]);
                    }
                    else if (character == '"')
                    {
                        isClosed = true;
                        break;
                    }
                    else
                    {
                        value.Append(character);
                    }
                }

                if (!isClosed)
                {
                    error = $"The value of '{key}' has no closing quote.";
                    return false;
                }

                if (position < text.Length && !char.IsWhiteSpace(text[position]))
                {
                    error = $"Expected a space after the value of '{key}'.";
                    return false;
                }

                arguments.Add(new MarkdownBlockArgument(key, value.ToString(), true));
            }
            else
            {
                var valueStart = position;
                while (position < text.Length && !char.IsWhiteSpace(text[position]))
                {
                    position++;
                }

                var value = text[valueStart..position];
                if (value.Contains('"'))
                {
                    error = $"The value of '{key}' contains a quote. Put the whole value in quotes.";
                    return false;
                }

                arguments.Add(new MarkdownBlockArgument(key, value, false));
            }
        }

        head = new MarkdownBlockHead(fenceLength, name, id, arguments);
        return true;
    }

    public string Format()
    {
        var builder = new StringBuilder();
        builder.Append(MarkdownBlockFence.Character, FenceLength).Append(' ').Append(Name);
        if (Id is not null)
        {
            builder.Append('#').Append(Id);
        }

        foreach (var argument in Arguments)
        {
            builder.Append(' ').Append(argument.Key).Append('=');
            if (argument.IsQuoted || NeedsQuotes(argument.Value))
            {
                builder.Append('"')
                    .Append(argument.Value.Replace("\\", "\\\\").Replace("\"", "\\\""))
                    .Append('"');
            }
            else
            {
                builder.Append(argument.Value);
            }
        }

        return builder.ToString();
    }

    private static bool NeedsQuotes(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character is '"' or '\\')
            {
                return true;
            }
        }

        return false;
    }
}
