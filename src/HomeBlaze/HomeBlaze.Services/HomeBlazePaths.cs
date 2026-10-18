using Microsoft.Extensions.Configuration;

namespace HomeBlaze.Services;

/// <summary>
/// Setting keys, defaults and resolution of the HomeBlaze instance paths.
/// </summary>
public static class HomeBlazePaths
{
    /// <summary>Setting that points to the root configuration file.</summary>
    public const string RootConfigurationFileKey = "HomeBlaze:RootConfigFile";

    /// <summary>Setting that points to the folder copied into an empty data directory on first start, and whose documentation directory replaces the data directory's one on every start.</summary>
    public const string SeedDirectoryKey = "HomeBlaze:SeedDirectory";

    /// <summary>Setting that overrides the plugin configuration file.</summary>
    public const string PluginConfigurationPathKey = "PluginConfigurationPath";

    /// <summary>Setting that points to a PKCS#12 certificate with a private key that encrypts the Data Protection keys at rest.</summary>
    public const string DataProtectionCertificatePathKey = "HomeBlaze:DataProtection:CertificatePath";

    /// <summary>Setting that holds the password of the Data Protection certificate, if it has one.</summary>
    public const string DataProtectionCertificatePasswordKey = "HomeBlaze:DataProtection:CertificatePassword";

    /// <summary>Default root configuration file, relative to the working directory.</summary>
    public static readonly string DefaultRootConfigurationFile = Path.Combine("Data", "Root.json");

    /// <summary>Default plugin configuration file, relative to the data directory.</summary>
    public static readonly string DefaultPluginConfigurationFile = Path.Combine("Files", "Plugins.json");

    /// <summary>Folder of the ASP.NET Core Data Protection keys, relative to the data directory.</summary>
    public const string DataProtectionKeysDirectory = "DataProtection-Keys";

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

    /// <summary>
    /// Resolves the full path of the folder that keeps the ASP.NET Core Data Protection keys, in the data directory next to the root configuration file.
    /// </summary>
    public static string GetDataProtectionKeysPath(IConfiguration? configuration)
    {
        var dataDirectory = Path.GetDirectoryName(GetRootConfigurationPath(configuration))!;
        return Path.Combine(dataDirectory, DataProtectionKeysDirectory);
    }

    /// <summary>
    /// Resolves the full path of the certificate that encrypts the Data Protection keys, or <c>null</c> when none is configured.
    /// A relative setting resolves against the working directory.
    /// </summary>
    public static string? GetDataProtectionCertificatePath(IConfiguration configuration)
    {
        var certificatePath = configuration[DataProtectionCertificatePathKey];
        return string.IsNullOrWhiteSpace(certificatePath) ? null : Path.GetFullPath(certificatePath);
    }
}
