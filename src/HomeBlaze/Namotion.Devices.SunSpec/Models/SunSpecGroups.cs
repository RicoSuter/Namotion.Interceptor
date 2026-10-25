using Namotion.Devices.SunSpec.Definitions;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec.Models;

/// <summary>
/// Updates group subjects from a resolved layout.
/// </summary>
internal static class SunSpecGroups
{
    /// <summary>
    /// Gets the subjects of a repeating group, keeping an existing subject at the same position and address, and
    /// returning <paramref name="current"/> itself when nothing changed.
    /// </summary>
    public static TGroup[] Update<TGroup>(TGroup[] current, IReadOnlyList<SunSpecGroupInstance> instances, Func<SunSpecGroupInstance, TGroup> create)
        where TGroup : class, IModbusBaseAddressProvider
    {
        var result = new TGroup[instances.Count];
        var isUnchanged = current.Length == instances.Count;
        for (var index = 0; index < instances.Count; index++)
        {
            var instance = instances[index];
            var existing = index < current.Length && current[index].BaseAddress == instance.Address ? current[index] : null;
            isUnchanged &= existing is not null;

            var group = existing ?? create(instance);
            (group as ISunSpecGroupOwner)?.UpdateGroups(instance);
            result[index] = group;
        }

        return isUnchanged ? current : result;
    }

    /// <summary>
    /// Gets the subject of a group that occurs once, keeping <paramref name="current"/> when its address is unchanged,
    /// or <c>null</c> when there is no instance.
    /// </summary>
    public static TGroup? UpdateSingle<TGroup>(TGroup? current, IReadOnlyList<SunSpecGroupInstance> instances, Func<SunSpecGroupInstance, TGroup> create)
        where TGroup : class, IModbusBaseAddressProvider
    {
        if (instances.Count == 0)
        {
            return null;
        }

        var instance = instances[0];
        var group = current is not null && current.BaseAddress == instance.Address ? current : create(instance);
        (group as ISunSpecGroupOwner)?.UpdateGroups(instance);
        return group;
    }
}
