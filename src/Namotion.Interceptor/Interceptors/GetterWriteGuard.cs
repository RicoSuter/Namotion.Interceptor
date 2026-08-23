using System.Diagnostics;

namespace Namotion.Interceptor.Interceptors;

/// <summary>
/// Debug-only detection for the getter contract: a property getter must not write a subject-typed
/// property, and must not attach or detach a context. The read terminal runs user getter code (an
/// added property's getter delegate) while the subject's SyncRoot may be held, and a structural
/// write or an explicit attach or detach from there would take the lifecycle gate and the
/// attachment monitor after SyncRoot, inverting the structural lock order (gate, then attachment
/// monitor, then SyncRoot). Violations are a programming error, not a supported shape, so Release
/// builds compile the call sites out and pay no check.
/// </summary>
internal static class GetterWriteGuard
{
    [ThreadStatic]
    private static int _getterDepth;

    /// <summary>Marks the thread as executing a getter behind the read chain. Must be paired with
    /// <see cref="ExitGetter"/> in a finally block, because getters may throw.</summary>
    [Conditional("DEBUG")]
    public static void EnterGetter()
    {
        _getterDepth++;
    }

    [Conditional("DEBUG")]
    public static void ExitGetter()
    {
        _getterDepth--;
    }

    /// <summary>Called on entry of every structural write and of the explicit context attach and
    /// detach entry points, which take the same locks.</summary>
    [Conditional("DEBUG")]
    public static void ThrowIfInsideGetter()
    {
        if (_getterDepth > 0)
        {
            throw new InvalidOperationException(
                "A property getter must not write a subject-typed property or attach or detach a " +
                "context. The getter runs inside the read chain, where those operations would " +
                "invert the structural lock order (lifecycle gate, then attachment monitor, then " +
                "SyncRoot). Move the operation out of the getter. This check only exists in Debug " +
                "builds.");
        }
    }
}
