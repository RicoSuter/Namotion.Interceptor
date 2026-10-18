namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Where a subject type or a method is not offered, see <see cref="ExcludeFromAttribute"/>.
/// </summary>
[Flags]
public enum ExclusionTarget
{
    /// <summary>
    /// User interfaces: a type is not listed or counted as a child in the browser, not offered by the path picker and
    /// not walked by navigation; a method is not shown as a button and not offered by the path picker or the widget
    /// pickers.
    /// </summary>
    UserInterface = 1,

    /// <summary>
    /// Agents: a type is not listed by the MCP tools browse and search and the built-in agents; a method is not
    /// listed for them, and their calls of it are refused as not found.
    /// </summary>
    Agents = 2,

    /// <summary>
    /// Every target, also those added later.
    /// </summary>
    All = ~0
}
