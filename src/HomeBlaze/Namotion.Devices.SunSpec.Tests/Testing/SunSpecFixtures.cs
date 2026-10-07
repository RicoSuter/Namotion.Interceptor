using System.Text.Json;

namespace Namotion.Devices.SunSpec.Tests.Testing;

/// <summary>
/// Loads the pysunspec2 device dumps in Fixtures/pysunspec2.
/// </summary>
internal static class SunSpecFixtures
{
    /// <summary>
    /// Builds a chain of the models in <paramref name="modelIds"/>, leaving out the points named in
    /// <paramref name="ignoredPoints"/> (renamed in the SunSpec definitions since the dump was written).
    /// </summary>
    public static SunSpecTestChain LoadPySunSpec(string fileName, int[] modelIds, params string[] ignoredPoints)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pysunspec2", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var chain = new SunSpecTestChain();
        foreach (var model in document.RootElement.GetProperty("models").EnumerateArray())
        {
            var modelId = model.GetProperty("ID").GetInt32();
            if (modelIds.Contains(modelId))
            {
                chain.AddModel(modelId, ToValues(model, ignoredPoints));
            }
        }

        return chain;
    }

    private static Dictionary<string, object?> ToValues(JsonElement element, string[] ignoredPoints)
    {
        var values = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is "ID" or "L" || ignoredPoints.Contains(property.Name))
            {
                continue;
            }

            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out var number) ? number : (object)property.Value.GetUInt64(),
                JsonValueKind.Array => property.Value.EnumerateArray()
                    .Select(item => (IReadOnlyDictionary<string, object?>)ToValues(item, ignoredPoints))
                    .ToList(),
                var kind => throw new InvalidDataException($"Unexpected {kind} value for {property.Name}.")
            };
        }

        return values;
    }
}
