namespace HomeBlaze.Services;

/// <summary>
/// How the configuration properties of a subject are named in JSON, at every depth.
/// The <c>$type</c> discriminator is spelled the same in both namings.
/// </summary>
public enum ConfigurationNaming
{
    /// <summary>
    /// The property name in camelCase, or the explicit JSON name of a property that has one.
    /// This is the naming of configuration files.
    /// </summary>
    CamelCase,

    /// <summary>
    /// The property name as it is declared, also for a property with an explicit JSON name.
    /// This is the naming of the blocks on a markdown page.
    /// </summary>
    PropertyName
}
