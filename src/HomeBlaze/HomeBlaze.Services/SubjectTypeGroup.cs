namespace HomeBlaze.Services;

/// <summary>
/// A group of creatable subject types with those of its types that match the current search.
/// </summary>
/// <param name="Name">The name of the group.</param>
/// <param name="Matches">The matching types of the group in the order they are listed, which is empty when none matches.</param>
public sealed record SubjectTypeGroup(string Name, IReadOnlyList<SubjectTypeDescriptor> Matches);
