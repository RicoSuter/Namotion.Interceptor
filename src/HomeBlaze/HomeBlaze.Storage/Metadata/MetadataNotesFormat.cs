using HomeBlaze.Storage.Abstractions.Metadata;

namespace HomeBlaze.Storage.Metadata;

/// <summary>
/// The <see cref="IMetadataNotesFormat"/> of <see cref="MetadataNotes"/>.
/// </summary>
public sealed class MetadataNotesFormat : IMetadataNotesFormat
{
    /// <inheritdoc />
    public int MaxSize => MetadataNotes.MaxSize;

    /// <inheritdoc />
    public string? GetError(string notes) => MetadataNotes.GetError(notes);

    /// <inheritdoc />
    public string? ToHtml(string notes) => MetadataNotes.ToHtml(notes);

    /// <inheritdoc />
    public string ToText(string notes) => MetadataNotes.ToText(notes);
}
