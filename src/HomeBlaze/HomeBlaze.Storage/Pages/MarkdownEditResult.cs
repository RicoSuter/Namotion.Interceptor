namespace HomeBlaze.Storage.Pages;

/// <summary>Whether a change of the blocks of a page was written, and why not when it was refused.</summary>
/// <param name="Refusal">Why nothing was written, or null when the change was written.</param>
public readonly record struct MarkdownEditResult(string? Refusal)
{
    /// <summary>Gets whether the change was written.</summary>
    public bool IsWritten => Refusal is null;

    /// <summary>A change that was written.</summary>
    public static MarkdownEditResult Written => default;

    /// <summary>A change that was refused for <paramref name="refusal"/>.</summary>
    public static MarkdownEditResult Refused(string refusal) => new(refusal);
}
