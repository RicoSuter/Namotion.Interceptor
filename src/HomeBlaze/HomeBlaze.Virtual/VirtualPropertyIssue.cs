namespace HomeBlaze.Virtual;

/// <summary>An entry that does not apply, by its path in the tree, such as <c>Pv/Power@Maximum</c>.</summary>
public sealed record VirtualPropertyIssue(string Path, string Message);
