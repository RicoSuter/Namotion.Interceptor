using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Whether the caller of the current async flow may read a property, for values that reach the caller indirectly, such as
/// the expressions of a page. Code that shows such a value to a caller treats a property the check refuses as missing.
/// Without a scope on the flow every property may be read.
/// </summary>
public static class PropertyReadAccess
{
    private static readonly AsyncLocal<Func<RegisteredSubjectProperty, bool>?> CanReadOnFlow = new();

    /// <summary>Whether the property may be read on the current flow.</summary>
    /// <param name="property">The property whose value would be shown.</param>
    public static bool CanRead(RegisteredSubjectProperty property) => CanReadOnFlow.Value?.Invoke(property) ?? true;

    /// <summary>
    /// Sets the read check of the current flow until the returned scope is disposed, which restores the check before it.
    /// A caller that reads for someone sets it either way, so that a check of the flow it runs on never carries over.
    /// </summary>
    /// <param name="canRead">Whether a property may be read; called concurrently and must not throw. Null allows every property.</param>
    public static IDisposable Enter(Func<RegisteredSubjectProperty, bool>? canRead)
    {
        var previous = CanReadOnFlow.Value;
        CanReadOnFlow.Value = canRead;
        return new Scope(previous);
    }

    private sealed class Scope(Func<RegisteredSubjectProperty, bool>? previous) : IDisposable
    {
        public void Dispose() => CanReadOnFlow.Value = previous;
    }
}
