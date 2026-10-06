using System.Text;

namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// Names derived from SunSpec definitions, shared by the generated and the dynamic models.
/// </summary>
internal static class SunSpecNames
{
    /// <summary>
    /// Converts a group name such as "module", "Crv" or "lithium-ion-module-cell" into a property name
    /// ("Module", "Crv", "LithiumIonModuleCell").
    /// </summary>
    public static string ToPascalCase(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var part in name.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(char.ToUpperInvariant(part[0])).Append(part, 1, part.Length - 1);
        }

        return builder.ToString();
    }
}
