using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Namotion.Devices.SunSpec.Definitions;

namespace Namotion.Devices.SunSpec.Discovery;

/// <summary>
/// Loads user-supplied SunSpec model definitions for models without a generated class. Not thread-safe.
/// </summary>
internal sealed class SunSpecDefinitionDirectory
{
    // The definitions of the previous load by file content: an unchanged file yields the same instance, which keeps
    // the dynamic model subjects created from it.
    private Dictionary<string, SunSpecModelDefinition> _definitionsByContent = new(StringComparer.Ordinal);

    /// <summary>
    /// Loads every <c>*.json</c> file of <paramref name="directory"/>. Invalid or unreadable files, files for models with
    /// a generated class and duplicates are logged and ignored; a missing folder yields no definitions. A file whose
    /// content did not change since the previous load yields the same definition instance.
    /// </summary>
    public IReadOnlyDictionary<int, SunSpecModelDefinition> Load(string? directory, Func<int, bool> hasGeneratedClass, ILogger logger)
    {
        var definitions = new Dictionary<int, SunSpecModelDefinition>();
        var definitionsByContent = new Dictionary<string, SunSpecModelDefinition>(StringComparer.Ordinal);
        foreach (var file in GetFiles(directory, logger))
        {
            if (TryLoad(file, definitionsByContent, logger) is not { } definition)
            {
                continue;
            }

            if (hasGeneratedClass(definition.Id))
            {
                logger.LogWarning("Ignoring the SunSpec model definition {File}: model {ModelId} is built in.", file, definition.Id);
            }
            else if (!definitions.TryAdd(definition.Id, definition))
            {
                logger.LogWarning("Ignoring the SunSpec model definition {File}: model {ModelId} is already defined by another file.", file, definition.Id);
            }
        }

        _definitionsByContent = definitionsByContent;
        return definitions;
    }

    private static string[] GetFiles(string? directory, ILogger logger)
    {
        if (directory is null || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var files = Directory.GetFiles(directory, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Cannot list the SunSpec model definitions in {Directory}.", directory);
            return [];
        }
    }

    private SunSpecModelDefinition? TryLoad(string file, Dictionary<string, SunSpecModelDefinition> definitionsByContent, ILogger logger)
    {
        try
        {
            var content = File.ReadAllText(file);
            if (!definitionsByContent.TryGetValue(content, out var definition) &&
                !_definitionsByContent.TryGetValue(content, out definition))
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
                definition = SunSpecDefinitions.Parse(stream);
            }

            definitionsByContent[content] = definition;
            return definition;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Ignoring the invalid SunSpec model definition {File}.", file);
            return null;
        }
    }
}
