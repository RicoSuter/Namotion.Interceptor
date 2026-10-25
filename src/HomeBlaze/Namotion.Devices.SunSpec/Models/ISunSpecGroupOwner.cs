using Namotion.Devices.SunSpec.Definitions;

namespace Namotion.Devices.SunSpec.Models;

/// <summary>
/// A model or group subject with nested groups whose instances depend on the registers read during discovery.
/// </summary>
internal interface ISunSpecGroupOwner
{
    /// <summary>
    /// Creates, keeps or removes the nested group subjects to match <paramref name="instance"/>.
    /// </summary>
    void UpdateGroups(SunSpecGroupInstance instance);
}
