namespace HomeBlaze.Storage;

/// <summary>
/// Grants the permission to edit page layout to the Markdown write operations that run within a scope on the current
/// async flow, such as those of an agent that has the permission: layout blocks may then be added, changed, removed and
/// moved, while every other block must stay as it is.
/// </summary>
public static class MarkdownPageLayoutPermission
{
    private static readonly AsyncLocal<bool> IsGrantedOnFlow = new();

    /// <summary>Whether the permission is granted on the current flow.</summary>
    public static bool IsGranted => IsGrantedOnFlow.Value;

    /// <summary>
    /// Sets whether the permission is granted on the current flow until the returned scope is disposed. A caller that
    /// writes pages for someone sets it either way, so that a grant of the flow it runs on never carries over.
    /// </summary>
    public static IDisposable Enter(bool isGranted)
    {
        var wasGranted = IsGrantedOnFlow.Value;
        IsGrantedOnFlow.Value = isGranted;
        return new Scope(wasGranted);
    }

    private sealed class Scope(bool wasGranted) : IDisposable
    {
        public void Dispose() => IsGrantedOnFlow.Value = wasGranted;
    }
}
