namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// Distinct role names, sorted and compared ignoring case: the key of a cached policy.
/// </summary>
internal sealed class RoleNameSet : IEquatable<RoleNameSet>
{
    private readonly int _hashCode;

    private RoleNameSet(string[] names)
    {
        Names = names;
        var hash = new HashCode();
        foreach (var name in names)
        {
            hash.Add(name, StringComparer.OrdinalIgnoreCase);
        }

        _hashCode = hash.ToHashCode();
    }

    /// <summary>The names, one per name ignoring case, sorted ignoring case.</summary>
    public string[] Names { get; }

    /// <summary>Creates the set, or returns null when a name is null, empty or whitespace.</summary>
    public static RoleNameSet? TryCreate(IReadOnlyCollection<string> roleNames)
    {
        var names = new List<string>(roleNames.Count);
        foreach (var name in roleNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        var distinct = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (distinct.Count == 0 || !string.Equals(distinct[^1], name, StringComparison.OrdinalIgnoreCase))
            {
                distinct.Add(name);
            }
        }

        return new RoleNameSet([.. distinct]);
    }

    public bool Equals(RoleNameSet? other)
    {
        if (other is null || other._hashCode != _hashCode || other.Names.Length != Names.Length)
        {
            return false;
        }

        for (var index = 0; index < Names.Length; index++)
        {
            if (!string.Equals(Names[index], other.Names[index], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RoleNameSet);

    public override int GetHashCode() => _hashCode;
}
