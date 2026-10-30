namespace HomeBlaze;

/// <summary>Host settings a caller supplies instead of configuration files or environment variables.</summary>
/// <remarks>A null property leaves the corresponding configuration value untouched, so the normal
/// configuration sources still apply. Use <see cref="SuppressSeeding"/> to switch seeding off outright.</remarks>
public sealed record HomeBlazeWebApplicationOptions
{
    /// <summary>Full path of the root configuration file. Sets <c>HomeBlaze:RootConfigFile</c>.</summary>
    public string? RootConfigurationFile { get; init; }

    /// <summary>Seed folder. Sets <c>HomeBlaze:SeedDirectory</c>.</summary>
    public string? SeedDirectory { get; init; }

    /// <summary>
    /// Sets <c>HomeBlaze:SeedDirectory</c> to an empty value, which switches off both the first-start
    /// seeding and the documentation replacement. Takes precedence over <see cref="SeedDirectory"/>.
    /// </summary>
    public bool SuppressSeeding { get; init; }

    /// <summary>Plugin configuration file. Sets <c>PluginConfigurationPath</c>.</summary>
    public string? PluginConfigurationPath { get; init; }

    /// <summary>File to append logs to, or null to leave logging as configured.</summary>
    public string? LogFilePath { get; init; }

    /// <summary>Content root, under which the web root is <c>wwwroot</c>. Null keeps the working directory.</summary>
    public string? ContentRootPath { get; init; }

    /// <summary>
    /// Serves static web assets from the project folders they were built in, as the Development environment
    /// does, whenever the build output holds the manifest that maps them. Published output holds no such
    /// manifest, so there it has no effect.
    /// </summary>
    public bool UseBuildStaticWebAssets { get; init; }

    /// <summary>Addresses to bind. Empty keeps whatever the configuration specifies.</summary>
    public IReadOnlyList<string> Urls { get; init; } = [];

    /// <summary>Arguments passed to the configuration builder.</summary>
    public IReadOnlyList<string> CommandLineArguments { get; init; } = [];
}
