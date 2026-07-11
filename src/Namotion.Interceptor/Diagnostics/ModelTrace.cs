using System;
using System.Diagnostics;
using System.Threading;

namespace Namotion.Interceptor.Diagnostics;

/// <summary>
/// Emits abstract-state transition events for formal trace validation. All calls
/// are compiled out unless the MODELTRACE symbol is defined, so production ships
/// with no instrumentation. Test infrastructure installs <see cref="Sink"/>.
/// </summary>
public static class ModelTrace
{
    /// <summary>Test-side receiver of (field, itemKey, value). Null in production.</summary>
    public static readonly AsyncLocal<Action<string, string?, string>?> Sink = new();

    /// <summary>Records a scalar variable transition (field = value).</summary>
    [Conditional("MODELTRACE")]
    public static void Set(string field, string value) => Sink.Value?.Invoke(field, null, value);

    /// <summary>Records a per-item (map variable) transition (field[key] = value).</summary>
    [Conditional("MODELTRACE")]
    public static void SetItem(string field, string key, string value) => Sink.Value?.Invoke(field, key, value);
}
