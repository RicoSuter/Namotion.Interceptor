namespace HomeBlaze.Storage.Abstractions.Metadata;

/// <summary>
/// Checks and renders the <c>notes</c> of metadata under the rules that the storage applies on write.
/// </summary>
public interface IMetadataNotesFormat
{
    /// <summary>
    /// The largest notes, in bytes of UTF-8.
    /// </summary>
    int MaxSize { get; }

    /// <summary>
    /// Gets why the notes are refused, naming the rule they break, or null when they may be written.
    /// </summary>
    string? GetError(string notes);

    /// <summary>
    /// Converts the notes to HTML that is safe to insert into the page, with a link to a web address that opens in a new
    /// tab without opener and referrer; null for notes that break the rules, such as after an edit of the file by hand,
    /// which are to be shown as text with <see cref="ToText"/> instead.
    /// </summary>
    string? ToHtml(string notes);

    /// <summary>
    /// Gets the notes to show as text, as notes that <see cref="ToHtml"/> does not convert are: the first
    /// <see cref="MaxSize"/> bytes of UTF-8 of them, ending at a whole character.
    /// </summary>
    string ToText(string notes);
}
