using Microsoft.Extensions.Configuration;

namespace HomeBlaze.Services;

/// <summary>
/// Setting keys, defaults and resolution of the HomeBlaze instance paths.
/// </summary>
public static class HomeBlazePaths
{
    /// <summary>Setting that points to the root configuration file.</summary>
    public const string RootConfigurationFileKey = "HomeBlaze:RootConfigFile";

    /// <summary>Setting that points to the folder copied into an empty data directory on first start.</summary>
    public const string SeedDirectoryKey = "HomeBlaze:SeedDirectory";

    /// <summary>Setting that overrides the plugin configuration file.</summary>
    public const string PluginConfigurationPathKey = "PluginConfigurationPath";

    /// <summary>Default root configuration file, relative to the working directory.</summary>
    public static readonly string DefaultRootConfigurationFile = Path.Combine("Data", "Root.json");

    /// <summary>Default plugin configuration file, relative to the data directory.</summary>
    public static readonly string DefaultPluginConfigurationFile = Path.Combine("Files", "Plugins.json");

    /// <summary>
    /// Resolves the full path of the root configuration file. A relative setting resolves against the working directory.
    /// </summary>
    public static string GetRootConfigurationPath(IConfiguration? configuration)
    {
        var rootConfigFile = configuration?[RootConfigurationFileKey];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(rootConfigFile) ? DefaultRootConfigurationFile : rootConfigFile);
    }

    /// <summary>
    /// Resolves the full path of the plugin configuration file. A relative setting resolves against the data directory.
    /// </summary>
    public static string GetPluginConfigurationPath(IConfiguration configuration)
    {
        var dataDirectory = Path.GetDirectoryName(GetRootConfigurationPath(configuration))!;
        var pluginConfigurationPath = configuration[PluginConfigurationPathKey];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(pluginConfigurationPath) ? DefaultPluginConfigurationFile : pluginConfigurationPath, dataDirectory);
    }
}
