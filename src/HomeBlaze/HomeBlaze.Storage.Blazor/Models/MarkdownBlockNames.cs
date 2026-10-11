using HomeBlaze.Components.Dialogs;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Checks the name that the editor gives a new block, before the block is written into the page.
/// </summary>
public static class MarkdownBlockNames
{
    /// <summary>What a valid name is, as a sentence for the user.</summary>
    public const string Rule = "A name starts with a letter or '_' and contains only letters, digits, '_' and '-'.";

    /// <summary>
    /// Gets how the create subject dialog asks for the name of a new block of the page text: the name must be
    /// a valid block name, and the names that the blocks of the page already use are taken.
    /// </summary>
    public static SubjectNameOptions CreateNameOptions(string pageText)
    {
        return new SubjectNameOptions("Block name", "", GetUsedNames(pageText)) { Rule = GetError };
    }

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
    /// Gets the message for a name that no block can have, or null when the name is a valid block name.
    /// </summary>
    public static string? GetError(string name)
    {
        return MarkdownBlockHead.IsValidId(name)
            ? null
            : $"'{name}' cannot be used as a name on a page. {Rule}";
    }
}
