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

    /// <summary>Grants the permission on the current flow until the returned scope is disposed.</summary>
    public static IDisposable Grant()
    {
        var wasGranted = IsGrantedOnFlow.Value;
        IsGrantedOnFlow.Value = true;
        return new Scope(wasGranted);
    }

    private sealed class Scope(bool wasGranted) : IDisposable
    {
        public void Dispose() => IsGrantedOnFlow.Value = wasGranted;
    }
}
