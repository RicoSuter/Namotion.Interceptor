using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace HomeBlaze.Authorization.Internal;

/// <summary>
/// The role names of principals, which are compared by reference. Thread-safe.
/// </summary>
internal sealed class PrincipalRoleNames
{
    private readonly ConcurrentDictionary<object, ImmutableArray<string>> _principals = new(ReferenceEqualityComparer.Instance);

    /// <summary>Records the role names of a principal; <c>null</c> names remove it.</summary>
    public void Set(object principal, IReadOnlyCollection<string>? roleNames)
    {
        if (roleNames is null)
        {
            _principals.TryRemove(principal, out _);
        }
        else
        {
            _principals[principal] = [.. roleNames];
        }
    }

    /// <summary>Counts the principals naming <paramref name="roleName"/>, ignoring case.</summary>
    public int Count(string roleName)
    {
        var count = 0;
        foreach (var (_, roleNames) in _principals)
        {
            foreach (var name in roleNames)
            {
                if (string.Equals(name, roleName, StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>Counts the principals naming each role, ignoring case, a principal once per name.</summary>
    public Dictionary<string, int> CountPerName()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var namesOfPrincipal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, roleNames) in _principals)
        {
            namesOfPrincipal.Clear();
            foreach (var name in roleNames)
            {
                if (name is not null && namesOfPrincipal.Add(name))
                {
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                }
            }
        }

        return counts;
    }
}
