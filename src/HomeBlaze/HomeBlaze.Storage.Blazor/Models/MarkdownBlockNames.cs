using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Checks the name that the editor gives a new block, before the block is written into the page.
/// </summary>
public static class MarkdownBlockNames
{
    /// <summary>What a valid name is, as a sentence for the user.</summary>
    public const string Rule = "A name starts with a letter or '_' and contains only letters, digits, '_' and '-'.";

    /// <summary>Gets the names that the blocks of the page text already use.</summary>
    public static IReadOnlySet<string> GetUsedNames(string pageText)
    {
        var names = new HashSet<string>();
        foreach (var block in MarkdownPageParser.Parse(pageText).AllBlocks)
        {
            if (block.Head?.Id is { } id)
            {
                names.Add(id);
            }
        }

        return names;
    }

    /// <summary>
    /// Gets the message for a name that a new block cannot have, or null when the name can be used.
    /// </summary>
    public static string? GetError(string name, IReadOnlySet<string> usedNames)
    {
        if (!MarkdownBlockHead.IsValidId(name))
        {
            return $"'{name}' cannot be used as a name on a page. {Rule}";
        }

        // A second block with the same name renders as an error.
        return usedNames.Contains(name) ? $"The name '{name}' is already used on this page." : null;
    }
}
