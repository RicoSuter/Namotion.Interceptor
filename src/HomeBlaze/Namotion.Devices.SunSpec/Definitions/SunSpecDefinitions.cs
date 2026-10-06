using System.Collections.Concurrent;
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

        var points = definition.Group.Points;
        if (points.Count < 2 || points[0].Name != "ID" || points[1].Name != "L")
        {
            throw new JsonException($"Model {definition.Id} does not start with the ID and L points.");
        }

        ValidateGroup(definition.Id, definition.Group, isTopLevel: true);
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

        foreach (var child in group.Groups)
        {
            if (child.Count.IsFill && !isTopLevel)
            {
                throw new JsonException($"Group {child.Name} of model {modelId} fills the remaining length but is nested.");
            }

            ValidateGroup(modelId, child, isTopLevel: false);
        }
    }
}
