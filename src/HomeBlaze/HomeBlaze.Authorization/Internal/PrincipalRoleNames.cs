using System.Collections.Concurrent;
using System.Collections.Immutable;
using HomeBlaze.Abstractions;

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

    /// <summary>Gets the role names of every principal, labeled with its title or else its type name.</summary>
    public List<PrincipalRoleSet> GetRoleSets()
    {
        var roleSets = new List<PrincipalRoleSet>(_principals.Count);
        foreach (var (principal, roleNames) in _principals)
        {
            var label = (principal as ITitleProvider)?.Title ?? principal.GetType().Name;
            roleSets.Add(new PrincipalRoleSet(label, roleNames));
        }

        return roleSets;
    }

    /// <summary>Counts the principals naming each role, a principal once per name.</summary>
    public Dictionary<string, int> CountPerName()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var namesOfPrincipal = new HashSet<string>(StringComparer.Ordinal);
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
