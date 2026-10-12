using System.Globalization;
using System.Text;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// The rules shared by the operations that write Markdown files: local storage only, a size limit, plain file names and
/// no added or changed active content.
/// </summary>
internal static class MarkdownWriteRules
{
    private static readonly char[] InvalidFileNameCharacters = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Whether the storage is a <see cref="FluentStorageContainer"/> on the local disk or in memory, or a folder of one.
    /// </summary>
    public static bool IsLocal(IStorageContainer storage) => storage switch
    {
        FluentStorageContainer container => container.IsLocal,
        VirtualFolder folder => IsLocal(folder.Storage),
        _ => false
    };

    /// <exception cref="InvalidOperationException">The storage is not local.</exception>
    public static void EnsureLocal(IStorageContainer storage)
    {
        if (!IsLocal(storage))
        {
            throw CreateNotLocalException();
        }
    }

    public static InvalidOperationException CreateNotLocalException() =>
        new("Only Markdown files in local storage (on disk or in memory) can be written; this storage is not local.");

    /// <summary>
    /// Encodes the content as UTF-8.
    /// </summary>
    /// <exception cref="ArgumentException">The content is null or larger than <see cref="MarkdownFile.MaxContentSize"/> bytes.</exception>
    public static byte[] GetContentBytes(string content, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(content, parameterName);

        var size = Encoding.UTF8.GetByteCount(content);
        if (size > MarkdownFile.MaxContentSize)
        {
            throw new ArgumentException(
                $"The Markdown source would be {size} bytes, more than the limit of {MarkdownFile.MaxContentSize} bytes.", parameterName);
        }

        return Encoding.UTF8.GetBytes(content);
    }

    private const string ActiveContentMessage =
        $"Embedded subjects, {MarkdownFile.ActiveContent} cannot be added or changed through these operations; use the page editor.";

    /// <summary>
    /// Ensures that the new source of a page keeps its embedded subject blocks exactly as they are and adds none of the
    /// <see cref="MarkdownFile.ActiveContent"/> that the old source does not already contain. A new page passes an empty
    /// old source.
    /// </summary>
    /// <exception cref="ArgumentException">The new source adds or changes such content.</exception>
    public static void EnsureActiveContentUnchanged(string oldSource, string newSource, string parameterName)
    {
        var (oldBlocks, oldMarkup) = MarkdownContentParser.GetActiveContent(oldSource);
        var (newBlocks, newMarkup) = MarkdownContentParser.GetActiveContent(newSource);

        // Adding, changing or removing a block would create, reconfigure or detach a running subject.
        var blocksUnchanged = oldBlocks.Order(StringComparer.Ordinal).SequenceEqual(newBlocks.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (!blocksUnchanged || !IsContainedIn(newMarkup, oldMarkup))
        {
            throw new ArgumentException(ActiveContentMessage, parameterName);
        }
    }

    // Whether every item of the new list has its own occurrence in the old list.
    private static bool IsContainedIn(List<string> newItems, List<string> oldItems)
    {
        var available = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in oldItems)
        {
            available[item] = available.GetValueOrDefault(item) + 1;
        }

        foreach (var item in newItems)
        {
            if (available.GetValueOrDefault(item) == 0)
            {
                return false;
            }

            available[item]--;
        }

        return true;
    }

    /// <exception cref="ArgumentException">The name is not a plain file name ending in .md that becomes a visible page.</exception>
    public static void ValidateFileName(string name, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(name, parameterName);

        var isValid =
            name.Length > FileExtensions.Markdown.Length &&
            name.Length <= 255 &&
            name.EndsWith(FileExtensions.Markdown, StringComparison.OrdinalIgnoreCase) &&
            name == name.Trim() &&
            !name.StartsWith('.') &&
            name.IndexOfAny(InvalidFileNameCharacters) < 0 &&
            !name.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == UnicodeCategory.Format) &&
            !StoragePathFilter.IsTemporaryFile(name);

        if (!isValid)
        {
            throw new ArgumentException(
                $"'{name}' is not a valid Markdown file name: use a plain file name ending in .md, without folders, " +
                "a leading dot, invisible formatting characters or any of the characters / \\ : * ? \" < > |.", parameterName);
        }
    }
}
