using System.Text;

namespace HomeBlaze.Components.Inputs;

/// <summary>The size, the error and the preview of the text of a <see cref="MarkdownField"/>.</summary>
internal sealed record MarkdownFieldModel(int Size, bool IsTooLarge, string? Error, string? Html)
{
    /// <summary>The model of empty text: no preview and no error.</summary>
    public static MarkdownFieldModel Empty { get; } = new(0, false, null, null);

    /// <summary>
    /// Gets the model of the text: empty text has no preview and no error, too large text has an error before that of the
    /// renderer, and text with an error has no preview.
    /// </summary>
    public static MarkdownFieldModel Create(string? text, Func<string, string?> toHtml, Func<string, string?> getError, int? maxSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Empty;
        }

        var size = Encoding.UTF8.GetByteCount(text);
        if (size > maxSize)
        {
            return new MarkdownFieldModel(size, true, $"The text is {size} bytes, more than the {maxSize} allowed.", null);
        }

        var error = getError(text);
        return new MarkdownFieldModel(size, false, error, error is null ? toHtml(text) : null);
    }
}
