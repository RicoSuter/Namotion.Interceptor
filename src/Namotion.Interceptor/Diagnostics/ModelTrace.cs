using System;
using System.Diagnostics;
using System.Threading;

namespace Namotion.Interceptor.Diagnostics;

/// <summary>Test-side receiver of trace events. Null in production.</summary>
public interface IModelTraceSink
{
    /// <summary>Records that a variable (or a map variable's item) changed.</summary>
    void Record(string field, string? key, string value);

    /// <summary>Closes the current transition; the sink snapshots the current state.</summary>
    void Commit();
}

/// <summary>
/// Emits abstract-state transition events for formal trace validation. All calls are
/// compiled out unless MODELTRACE is defined, so production ships with no instrumentation.
/// A transition sets one or more fields via <see cref="Set"/>/<see cref="SetItem"/> then
/// closes with <see cref="Commit"/>; model actions change several variables atomically, so
/// a snapshot is taken per Commit, not per field. Test infrastructure installs <see cref="Sink"/>.
/// </summary>
public static class ModelTrace
{
    /// <summary>Test-side sink; null in production.</summary>
    public static readonly AsyncLocal<IModelTraceSink?> Sink = new();

    /// <summary>Records a scalar variable transition (field = value).</summary>
    [Conditional("MODELTRACE")]
    public static void Set(string field, string value) => Sink.Value?.Record(field, null, value);

    /// <summary>Records a per-item (map variable) transition (field[key] = value).</summary>
    [Conditional("MODELTRACE")]
    public static void SetItem(string field, string key, string value) => Sink.Value?.Record(field, key, value);

    /// <summary>Closes the current transition; the sink takes a snapshot.</summary>
    [Conditional("MODELTRACE")]
    public static void Commit() => Sink.Value?.Commit();
}
