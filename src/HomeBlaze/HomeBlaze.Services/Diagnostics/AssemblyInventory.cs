using System.Collections.Immutable;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// An assembly loaded in the process. <see cref="RepositoryUrl"/> is normalized by <see cref="BuildInformation"/>.
/// </summary>
internal readonly record struct LoadedAssembly(string Name, string LoadContext, string Version, string? Commit, string? RepositoryUrl);

internal sealed record InventoryAssembly(string Key, string Name, string LoadContext, string Version, string? Commit, bool HasMismatch);

internal sealed record InventoryRepository(string Name, string? RepositoryUrl, bool HasMismatch, ImmutableArray<InventoryAssembly> Assemblies)
{
    public bool HasAnyMismatch => HasMismatch || Assemblies.Any(assembly => assembly.HasMismatch);
}

/// <summary>
/// Groups loaded assemblies by repository and marks inconsistent builds.
/// </summary>
internal static class AssemblyInventory
{
    public const string DefaultLoadContextName = "Default";
    public const string UnknownRepositoryName = "Unknown";

    private const int ShortCommitLength = 7;

    /// <summary>
    /// Builds the inventory. An assembly name loaded with different builds marks those assemblies, and more than one
    /// commit within <paramref name="ownRepositoryUrl"/> marks that repository. A missing commit never marks anything.
    /// </summary>
    public static ImmutableArray<InventoryRepository> Create(IReadOnlyList<LoadedAssembly> assemblies, string? ownRepositoryUrl)
    {
        var mismatchedNames = FindNamesWithDifferentBuilds(assemblies);

        var groups = new SortedDictionary<string, List<LoadedAssembly>>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in assemblies)
        {
            var key = assembly.RepositoryUrl ?? string.Empty;
            if (!groups.TryGetValue(key, out var members))
            {
                members = [];
                groups[key] = members;
            }

            members.Add(assembly);
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositories = new List<InventoryRepository>(groups.Count);
        foreach (var (key, members) in groups)
        {
            var repositoryUrl = key.Length > 0 ? key : null;
            var isOwn = repositoryUrl is not null && string.Equals(repositoryUrl, ownRepositoryUrl, StringComparison.OrdinalIgnoreCase);
            var hasMismatch = isOwn && HasConflictingCommits(members);

            var inventoryAssemblies = members
                .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.LoadContext != DefaultLoadContextName)
                .ThenBy(member => member.LoadContext, StringComparer.OrdinalIgnoreCase)
                .Select(member => new InventoryAssembly(
                    GetUniqueKey(member, usedKeys), member.Name, member.LoadContext, member.Version, member.Commit, mismatchedNames.Contains(member.Name)))
                .ToImmutableArray();

            repositories.Add(new InventoryRepository(GetUniqueName(repositoryUrl, usedNames), repositoryUrl, hasMismatch, inventoryAssemblies));
        }

        return repositories
            .OrderBy(repository => repository.RepositoryUrl is null)
            .ThenBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static HashSet<string> FindNamesWithDifferentBuilds(IReadOnlyList<LoadedAssembly> assemblies)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var firstIndex = 0; firstIndex < assemblies.Count; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < assemblies.Count; secondIndex++)
            {
                if (string.Equals(assemblies[firstIndex].Name, assemblies[secondIndex].Name, StringComparison.OrdinalIgnoreCase) &&
                    IsDifferentBuild(assemblies[firstIndex], assemblies[secondIndex]))
                {
                    names.Add(assemblies[firstIndex].Name);
                }
            }
        }

        return names;
    }

    private static bool IsDifferentBuild(LoadedAssembly first, LoadedAssembly second)
    {
        // An empty version comes from a failed attribute read (see LoadedAssemblyReader); it must never trigger a warning.
        var hasDifferentVersion = first.Version.Length > 0 && second.Version.Length > 0 &&
            !string.Equals(first.Version, second.Version, StringComparison.OrdinalIgnoreCase);

        return hasDifferentVersion || HasDifferentCommits(first.Commit, second.Commit);
    }

    // Prefix equality is transitive, so comparing every known commit against the first one found is enough to detect
    // any pairwise mismatch among them.
    private static bool HasConflictingCommits(List<LoadedAssembly> members)
    {
        string? firstCommit = null;
        foreach (var member in members)
        {
            if (member.Commit is not { Length: >= ShortCommitLength } commit)
            {
                continue;
            }

            if (firstCommit is null)
            {
                firstCommit = commit;
            }
            else if (!ArePrefixesEqual(firstCommit, commit))
            {
                return true;
            }
        }

        return false;
    }

    // A missing or too-short commit is unknown and never marks a mismatch; only two known, differing commits do.
    private static bool HasDifferentCommits(string? first, string? second) =>
        first is { Length: >= ShortCommitLength } && second is { Length: >= ShortCommitLength } &&
        !ArePrefixesEqual(first, second);

    private static bool ArePrefixesEqual(string first, string second) =>
        string.Compare(first, 0, second, 0, ShortCommitLength, StringComparison.OrdinalIgnoreCase) == 0;

    private static string GetKey(LoadedAssembly assembly) =>
        assembly.LoadContext == DefaultLoadContextName ? assembly.Name : $"{assembly.Name} ({assembly.LoadContext})";

    private static string GetUniqueName(string? repositoryUrl, HashSet<string> usedNames) =>
        MakeUnique(repositoryUrl is null ? UnknownRepositoryName : repositoryUrl[(repositoryUrl.LastIndexOf('/') + 1)..], usedNames);

    // Load context names are not unique across the process: two plugin contexts can share a name, unnamed contexts all
    // become "Unnamed", and a plugin context could even be named "Default". Keys must stay unique within the inventory.
    private static string GetUniqueKey(LoadedAssembly assembly, HashSet<string> usedKeys) =>
        MakeUnique(GetKey(assembly), usedKeys);

    private static string MakeUnique(string name, HashSet<string> usedNames)
    {
        var candidate = name;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{name} {suffix++}";
        }

        return candidate;
    }
}
