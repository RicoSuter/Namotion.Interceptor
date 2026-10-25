using System.Collections.Frozen;

namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// Property names of the members every model and group subject declares, which point and group properties must not use.
/// Shared by the generated and the dynamic models.
/// </summary>
internal static class SunSpecMemberNames
{
    /// <summary>Gets the member names of a model subject.</summary>
    public static FrozenSet<string> Model { get; } = FrozenSet.Create(StringComparer.Ordinal, "ModelId", "BaseAddress", "Length", "Title", "ModelIdRegister");

    /// <summary>Gets the member names of a group subject.</summary>
    public static FrozenSet<string> Group { get; } = FrozenSet.Create(StringComparer.Ordinal, "Parent", "BaseAddress", "Index", "Title");
}
