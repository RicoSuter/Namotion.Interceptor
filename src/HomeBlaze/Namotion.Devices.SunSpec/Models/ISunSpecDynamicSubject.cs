using System.Collections.Concurrent;

namespace Namotion.Devices.SunSpec.Models;

/// <summary>
/// A subject whose properties are added at runtime and backed by <see cref="Values"/>.
/// </summary>
internal interface ISunSpecDynamicSubject
{
    /// <summary>
    /// Gets the property values by property name. Concurrent because the source writes values while other threads read them.
    /// </summary>
    ConcurrentDictionary<string, object?> Values { get; }
}
