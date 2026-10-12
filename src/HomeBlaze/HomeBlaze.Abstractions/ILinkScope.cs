namespace HomeBlaze.Abstractions;

/// <summary>
/// A subject that resolves the links of everything it contains, such as a page for the widgets on it.
/// </summary>
public interface ILinkScope
{
    /// <summary>
    /// Returns the address a link leads to. A link that needs no resolving is returned as written.
    /// </summary>
    string ResolveLink(string link);
}
