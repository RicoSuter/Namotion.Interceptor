using Namotion.Interceptor;

namespace HomeBlaze.Storage.Pages;

/// <summary>Whether a change of the blocks or the text of a page was written, why not, and what it wrote.</summary>
/// <param name="Refusal">Why nothing was written, or null when the change was written.</param>
/// <param name="Subject">
/// The subject of the block that the change moved or inserted, or for a change of text the segment of the first block of
/// text it wrote, once the page describes the written text, which it does when the storage read the file inside the
/// write. Null otherwise, and for a deletion.
/// </param>
/// <remarks>
/// A change whose write the storage fails has no result: the exception of the storage propagates, and the page then
/// describes its file again, unless reading it failed as well.
/// </remarks>
public readonly record struct MarkdownEditResult(string? Refusal, IInterceptorSubject? Subject = null)
{
    /// <summary>Gets whether the change was written.</summary>
    public bool IsWritten => Refusal is null;

    /// <summary>A change that was written and names no subject.</summary>
    public static MarkdownEditResult Written => default;

    /// <summary>A change that was written, with the subject it names.</summary>
    public static MarkdownEditResult WrittenFor(IInterceptorSubject? subject) => new(null, subject);

    /// <summary>A change that was refused for <paramref name="refusal"/>.</summary>
    public static MarkdownEditResult Refused(string refusal) => new(refusal);
}
