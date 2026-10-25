using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec.Generator;

/// <summary>
/// Checks that the models of a family can share the class generated from their representative.
/// </summary>
internal static class SunSpecFamilyLayout
{
    // What shapes a generated property and its register mapping. Point names and labels are not part of it because
    // they legitimately differ between family members.
    private readonly record struct PointShape(string Type, int Size, MappedShape? Map);

    private readonly record struct MappedShape(
        ModbusDataType DataType, SunSpecValueKind Kind, decimal Scale, string? ScaleFactorPointName, StateUnit Unit,
        bool IsReadOnly, ModbusNotAvailableValue NotAvailableValue);

    /// <summary>
    /// Ensures <paramref name="model"/> has the same groups, register layout and point mappings as <paramref name="representative"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The layouts differ; the message names the models and the point or group.</exception>
    public static void EnsureSameLayout(SunSpecModelDefinition representative, SunSpecModelDefinition model)
    {
        var models = $"model {model.Id} and its class representative {representative.Id}";
        EnsureSameGroup(representative.Group, model.Group, representative.Group.Name, models);
    }

    private static void EnsureSameGroup(SunSpecGroupDefinition expected, SunSpecGroupDefinition actual, string path, string models)
    {
        if (expected.Count != actual.Count || expected.Points.Count != actual.Points.Count || expected.Groups.Count != actual.Groups.Count)
        {
            throw new InvalidDataException($"Group {path} differs in count, point count or group count between {models}.");
        }

        for (var index = 0; index < expected.Points.Count; index++)
        {
            EnsureSamePoint(expected.Points[index], actual.Points[index], path, models);
        }

        for (var index = 0; index < expected.Groups.Count; index++)
        {
            var expectedGroup = expected.Groups[index];
            var actualGroup = actual.Groups[index];
            if (expectedGroup.Name != actualGroup.Name)
            {
                throw new InvalidDataException($"Group {path}.{expectedGroup.Name} is named {actualGroup.Name} in one of {models}.");
            }

            EnsureSameGroup(expectedGroup, actualGroup, $"{path}.{expectedGroup.Name}", models);
        }
    }

    // The generated enum has the representative's symbols, so a member may leave some out (such as reserved bits)
    // but must not use a value the enum lacks.
    private static void EnsureSamePoint(SunSpecPointDefinition expected, SunSpecPointDefinition actual, string path, string models)
    {
        if (GetShape(expected) != GetShape(actual))
        {
            throw new InvalidDataException($"Point {expected.Name} of group {path} differs in type, size or mapping between {models}.");
        }

        var expectedValues = expected.Symbols.Select(symbol => symbol.Value).ToHashSet();
        if (actual.Symbols.FirstOrDefault(symbol => !expectedValues.Contains(symbol.Value)) is { } missingSymbol)
        {
            throw new InvalidDataException(
                $"Point {expected.Name} of group {path} differs between {models}: the representative lacks the symbol value {missingSymbol.Value}.");
        }
    }

    private static PointShape GetShape(SunSpecPointDefinition point)
    {
        var map = SunSpecPointMapping.TryMap(point);
        var mappedShape = map is null
            ? (MappedShape?)null
            : new MappedShape(map.DataType, map.Kind, map.Scale, map.ScaleFactorPointName, map.Unit, map.IsReadOnly, map.NotAvailableValue);

        return new PointShape(point.Type, point.Size, mappedShape);
    }
}
