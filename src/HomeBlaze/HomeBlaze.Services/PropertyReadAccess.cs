using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services;

/// <summary>
/// Whether the caller of the current async flow may read a property, and the value it is shown, for values that reach
/// the caller indirectly, such as the expressions of a page. Code that shows such a value to a caller treats a property
/// the check refuses as missing and shows <see cref="GetValue"/>. Without a scope on the flow every property may be read
/// and is shown as it is.
/// </summary>
public static class PropertyReadAccess
{
    private static readonly AsyncLocal<Access?> AccessOnFlow = new();

    /// <summary>Whether the property may be read on the current flow.</summary>
    /// <param name="property">The property whose value would be shown.</param>
    public static bool CanRead(RegisteredSubjectProperty property) => AccessOnFlow.Value?.CanRead?.Invoke(property) ?? true;

    /// <summary>
    /// Gets the value of the property as it is shown on the current flow: its value after the value filter of the scope,
    /// such as one that leaves out what the caller may not read. Call it only for a property <see cref="CanRead"/> allows.
    /// </summary>
    /// <param name="property">The property whose value is shown.</param>
    public static object? GetValue(RegisteredSubjectProperty property)
    {
        var value = property.GetValue();
        return AccessOnFlow.Value?.FilterValue is { } filterValue ? filterValue(property, value) : value;
    }

    /// <summary>
    /// Sets the read check and the value filter of the current flow until the returned scope is disposed, which restores
    /// those before it. A caller that reads for someone sets them either way, so that those of the flow it runs on never
    /// carry over.
    /// </summary>
    /// <param name="canRead">Whether a property may be read; called concurrently and must not throw. Null allows every property.</param>
    /// <param name="filterValue">Gets the value shown for a readable property from its value; called concurrently and
    /// must not throw. Null shows every value as it is.</param>
    public static IDisposable Enter(
        Func<RegisteredSubjectProperty, bool>? canRead, Func<RegisteredSubjectProperty, object?, object?>? filterValue = null)
    {
        var previous = AccessOnFlow.Value;
        AccessOnFlow.Value = canRead is null && filterValue is null ? null : new Access(canRead, filterValue);
        return new Scope(previous);
    }

    private sealed record Access(Func<RegisteredSubjectProperty, bool>? CanRead, Func<RegisteredSubjectProperty, object?, object?>? FilterValue);

    private sealed class Scope(Access? previous) : IDisposable
    {
        public void Dispose() => AccessOnFlow.Value = previous;
    }
}
