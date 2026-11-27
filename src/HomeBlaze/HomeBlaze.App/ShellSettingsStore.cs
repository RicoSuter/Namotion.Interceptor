using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeBlaze.App;

/// <summary>Reads and writes the shell's own settings file.</summary>
/// <remarks>A missing or unreadable file yields defaults rather than an exception: the shell must still start
/// so that the user can reach the bootstrap screen and fix the problem.</remarks>
public sealed class ShellSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public ShellSettingsStore(string filePath)
    {
        _filePath = filePath;
    }

    /// <summary>Default location: <c>&lt;ApplicationData&gt;/HomeBlaze/app-settings.json</c>.</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HomeBlaze",
        "app-settings.json");

    public ShellSettings Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<ShellSettings>(File.ReadAllText(_filePath), SerializerOptions) ?? new ShellSettings()
                : new ShellSettings();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ShellSettings();
        }
    }

    public void Save(ShellSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);

        // Written beside the target and moved into place, so a crash mid-write cannot leave a truncated
        // settings file that the next start would read as "no connections configured".
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }
}
