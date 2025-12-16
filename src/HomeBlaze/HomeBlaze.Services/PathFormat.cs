namespace HomeBlaze.Services;

/// <summary>
/// Path format for subject paths.
/// </summary>
public enum PathFormat
{
    /// <summary>
    /// Bracket notation: Children[demo].Children[file.json]
    /// Used by Widget, RenderExpression configurations.
    /// </summary>
    Bracket,

    /// <summary>
    /// Slash notation: Children/demo/Children/file.json
    /// Used by URLs and Blazor routing.
    /// </summary>
    Slash
}