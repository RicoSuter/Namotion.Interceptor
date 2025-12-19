using Namotion.Interceptor;

namespace HomeBlaze.Storage.Blazor.Models;

/// <summary>
/// Represents a decorated region in the Monaco editor (subject block or expression).
/// </summary>
/// <param name="StartLine">1-based start line number.</param>
/// <param name="StartColumn">1-based start column number.</param>
/// <param name="EndLine">1-based end line number.</param>
/// <param name="EndColumn">1-based end column number.</param>
/// <param name="Type">The type of decoration (SubjectBlock or Expression).</param>
/// <param name="Name">The name to display (e.g., "mymotor" for subject, "mymotor.Temperature" for expression).</param>
/// <param name="Subject">The resolved subject instance (for subject blocks only).</param>
public record DecorationRegion(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    DecorationRegionType Type,
    string Name,
    IInterceptorSubject? Subject = null)
{
    /// <summary>
    /// Checks if the given cursor position is inside this decoration region.
    /// </summary>
    /// <param name="line">1-based line number.</param>
    /// <param name="column">1-based column number.</param>
    /// <returns>True if the cursor is inside the region.</returns>
    public bool ContainsPosition(int line, int column)
    {
        // Before the start
        if (line < StartLine || (line == StartLine && column < StartColumn))
            return false;

        // After the end
        if (line > EndLine || (line == EndLine && column > EndColumn))
            return false;

        return true;
    }
}