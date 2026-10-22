using System.Net;
using System.Text;

namespace HomeBlaze.Components;

/// <summary>Names a block of text of a page for assistive technology: its kind and its first words, from its markup.</summary>
internal static class TextBlockNames
{
    private const int MaxLength = 40;
    private const string Ellipsis = "...";

    /// <summary>Gets a name such as "Heading: Motor Status", at most 40 characters long.</summary>
    public static string Describe(string html)
    {
        var kind = GetKind(html);
        var text = GetText(html);
        if (text.Length == 0)
        {
            return kind;
        }

        var available = MaxLength - kind.Length - 2;
        if (text.Length <= available)
        {
            return $"{kind}: {text}";
        }

        // Cut at the last space that leaves room for the ellipsis, or inside a word longer than that.
        var length = available - Ellipsis.Length;
        var space = text.LastIndexOf(' ', length);
        return $"{kind}: {text.AsSpan(0, space > 0 ? space : length)}{Ellipsis}";
    }

    // The kind of the first element, as the Markdown renderer writes it.
    private static string GetKind(string html)
    {
        var start = 0;
        while (start < html.Length && char.IsWhiteSpace(html[start]))
        {
            start++;
        }

        if (start == html.Length || html[start] != '<')
        {
            return "Text";
        }

        var end = start + 1;
        while (end < html.Length && char.IsAsciiLetterOrDigit(html[end]))
        {
            end++;
        }

        return html.AsSpan(start + 1, end - start - 1) switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "Heading",
            "p" => "Paragraph",
            "ul" or "ol" => "List",
            "table" => "Table",
            "blockquote" => "Quote",
            "pre" => "Code",
            "hr" => "Line",
            _ => "Text"
        };
    }

    // The text without the tags, with every run of white space as one space. A long block, such as a table, is read only as
    // far as the name can show.
    private static string GetText(string html)
    {
        var builder = new StringBuilder();
        var isInTag = false;
        foreach (var character in html)
        {
            if (isInTag)
            {
                isInTag = character != '>';
            }
            else if (character == '<')
            {
                isInTag = true;
            }
            else if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
            else if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            // Room for entities, such as &lt;, which become one character.
            if (builder.Length > MaxLength * 4)
            {
                break;
            }
        }

        return WebUtility.HtmlDecode(builder.ToString()).Trim();
    }
}
