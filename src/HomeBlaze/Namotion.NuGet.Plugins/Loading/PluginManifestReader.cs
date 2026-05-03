using System.Text.Json;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Reads and parses plugin.json from an extracted NuGet package directory.
/// </summary>
internal static class PluginManifestReader
{
    private const string PluginJsonFileName = "plugin.json";

    /// <summary>
    /// Reads plugin.json from the given extracted package directory.
    /// Returns null if the file does not exist or is malformed.
    /// </summary>
    public static JsonElement? Read(string extractedPackagePath)
    {
        var pluginJsonPath = Path.Combine(extractedPackagePath, PluginJsonFileName);
        if (!File.Exists(pluginJsonPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(pluginJsonPath);
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the hostDependencies array from a parsed plugin.json manifest.
    /// Returns an empty list if the field is missing or the manifest is null.
    /// </summary>
    public static IReadOnlyList<string> GetHostDependencies(JsonElement? manifest)
    {
        if (manifest == null)
        {
            return [];
        }

        if (manifest.Value.TryGetProperty("hostDependencies", out var hostDeps) &&
            hostDeps.ValueKind == JsonValueKind.Array)
        {
            return hostDeps.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()!)
                .ToList();
        }

        return [];
    }
}
