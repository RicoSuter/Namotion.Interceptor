using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// Parses SunSpec model definitions and provides the embedded SunSpec Alliance definitions.
/// </summary>
internal static class SunSpecDefinitions
{
    private const string ResourcePrefix = "SunSpec/model_";
    private const string ResourceSuffix = ".json";

    private static readonly ConcurrentDictionary<int, SunSpecModelDefinition?> BuiltInDefinitions = new();

    /// <summary>
    /// Parses and validates a definition.
    /// </summary>
    /// <exception cref="JsonException">The JSON is invalid or the definition is malformed.</exception>
    /// <exception cref="InvalidDataException">
    /// A point cannot be mapped, or a point or group property name is reserved or used twice in its group.
    /// </exception>
    public static SunSpecModelDefinition Parse(Stream stream)
    {
        var definition = JsonSerializer.Deserialize(stream, SunSpecDefinitionJsonContext.Default.SunSpecModelDefinition)
            ?? throw new JsonException("The model definition is empty.");

        Validate(definition);
        return definition;
    }

    /// <summary>
    /// Gets the embedded definition of <paramref name="modelId"/>, parsed once, or <c>null</c> when there is none.
    /// </summary>
    public static SunSpecModelDefinition? TryGetBuiltIn(int modelId)
        => BuiltInDefinitions.GetOrAdd(modelId, static id =>
        {
            using var stream = typeof(SunSpecDefinitions).Assembly.GetManifestResourceStream($"{ResourcePrefix}{id}{ResourceSuffix}");
            return stream is null ? null : Parse(stream);
        });

    /// <summary>
    /// Gets the IDs of all embedded definitions in ascending order.
    /// </summary>
    public static IReadOnlyList<int> GetBuiltInModelIds()
        => typeof(SunSpecDefinitions).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            .Select(name => int.Parse(name.AsSpan(ResourcePrefix.Length, name.Length - ResourcePrefix.Length - ResourceSuffix.Length), CultureInfo.InvariantCulture))
            .Order()
            .ToArray();

    private static void Validate(SunSpecModelDefinition definition)
    {
        if (definition.Id is < 1 or > 65534)
        {
            throw new JsonException($"Model ID {definition.Id} is outside 1 to 65534.");
        }

        ValidateNoNullElements(definition.Id, definition.Group);

        var points = definition.Group.Points;
        if (points.Count < 2 || points[0].Name != "ID" || points[1].Name != "L")
        {
            throw new JsonException($"Model {definition.Id} does not start with the ID and L points.");
        }

        ValidateGroup(definition.Id, definition.Group, isTopLevel: true);
    }

    // RespectNullableAnnotations does not cover collection elements, so null points, groups and symbols are rejected here.
    private static void ValidateNoNullElements(int modelId, SunSpecGroupDefinition group)
    {
        foreach (var point in group.Points)
        {
            if (point is null || point.Symbols.Any(symbol => symbol is null))
            {
                throw new JsonException($"Group {group.Name} of model {modelId} has a null point or symbol.");
            }
        }

        foreach (var child in group.Groups)
        {
            if (child is null)
            {
                throw new JsonException($"Group {group.Name} of model {modelId} has a null group.");
            }

            ValidateNoNullElements(modelId, child);
        }
    }

    private static void ValidateGroup(int modelId, SunSpecGroupDefinition group, bool isTopLevel)
    {
        foreach (var point in group.Points)
        {
            if (point.Size < 1)
            {
                throw new JsonException($"Point {point.Name} of model {modelId} has no size.");
            }
        }

        ValidatePropertyNames(modelId, group, isTopLevel);

        foreach (var child in group.Groups)
        {
            if (child.Count.IsFill && !isTopLevel)
            {
                throw new JsonException($"Group {child.Name} of model {modelId} fills the remaining length but is nested.");
            }

            ValidateGroup(modelId, child, isTopLevel: false);
        }
    }

    // The point and group properties of a subject must not replace each other or the subject's own members.
    private static void ValidatePropertyNames(int modelId, SunSpecGroupDefinition group, bool isTopLevel)
    {
        var reservedNames = isTopLevel ? SunSpecMemberNames.Model : SunSpecMemberNames.Group;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propertyPoint in SunSpecPointMapping.GetPropertyPoints(group, isTopLevel))
        {
            ValidatePropertyName(modelId, group, $"point {propertyPoint.Point.Name}", propertyPoint.Point.Name, reservedNames, names);
        }

        foreach (var child in group.Groups)
        {
            ValidatePropertyName(modelId, group, $"group {child.Name}", SunSpecNames.ToPascalCase(child.Name), reservedNames, names);
        }
    }

    private static void ValidatePropertyName(
        int modelId, SunSpecGroupDefinition group, string member, string propertyName, FrozenSet<string> reservedNames, HashSet<string> names)
    {
        if (reservedNames.Contains(propertyName))
        {
            throw new InvalidDataException($"The {member} in group {group.Name} of model {modelId} uses the reserved property name {propertyName}.");
        }

        if (!names.Add(propertyName))
        {
            throw new InvalidDataException($"The {member} in group {group.Name} of model {modelId} uses the property name {propertyName} more than once.");
        }
    }
}
