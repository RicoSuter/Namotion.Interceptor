using System.Collections.Concurrent;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Recorder;

/// <summary>
/// A utility class for property read recording and an optional interceptor.
/// For Blazor components, use explicit recording via RegisteredSubjectProperty.GetValueAndRecord()
/// since ambient context (AsyncLocal/ThreadLocal) doesn't flow through RenderFragment execution.
/// </summary>
public class ReadPropertyRecorder : IReadInterceptor
{
    /// <summary>
    /// Starts recording property reads to a new scope.
    /// Note: For Blazor components, prefer using RegisteredSubjectProperty.GetValueAndRecord()
    /// with the component's PropertyRecorder for explicit recording.
    /// </summary>
    /// <param name="context">The context to record for (kept for API compatibility).</param>
    /// <param name="properties">Optional preallocated properties dictionary.</param>
    /// <returns>The recording scope. Dispose to stop recording.</returns>
    public static ReadPropertyRecorderScope Start(IInterceptorSubjectContext context, ConcurrentDictionary<PropertyReference, bool>? properties = null)
    {
        return new ReadPropertyRecorderScope(context, properties);
    }

    /// <summary>
    /// Gets the current number of active scopes for a context (for debugging).
    /// Note: Returns 0 as ambient context-based recording is no longer used.
    /// </summary>
    [Obsolete("Ambient context-based recording is no longer used. Use explicit recording with GetValueAndRecord() instead.")]
    public static int GetActiveScopeCount(IInterceptorSubjectContext context)
    {
        return 0;
    }

    /// <summary>
    /// Gets the current number of active scopes (for debugging).
    /// Returns -1 to indicate this method is no longer valid without context.
    /// </summary>
    [Obsolete("Use GetActiveScopeCount(context) instead.")]
    public static int ActiveScopeCount => -1;

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        // No ambient recording - explicit recording via GetValueAndRecord() is preferred
        return next(ref context);
    }
}
