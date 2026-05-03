namespace HomeBlaze.Services;

/// <summary>
/// Path style for subject paths.
/// </summary>
public enum PathStyle
{
    /// <summary>
    /// Canonical notation with leading slash: /Demo/Setup.md/Temperature, /Items[0]/Name
    /// [InlinePaths] dictionary keys become path segments directly.
    /// Non-InlinePaths collection/dictionary indices use brackets on the property name.
    /// Used by UI, JSON config, MCP, markdown expressions.
    /// </summary>
    Canonical,

    /// <summary>
    /// Route notation with leading slash, flat segments: /Demo/Setup.md/Temperature, /Items/0/Name
    /// All indices become separate path segments (no brackets).
    /// Used by browser URL routing only.
    /// </summary>
    Route
}
