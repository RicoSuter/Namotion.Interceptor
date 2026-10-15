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
        var repositories = new List<InventoryRepository>(groups.Count);
        foreach (var (key, members) in groups)
        {
            var repositoryUrl = key.Length > 0 ? key : null;
            var isOwn = repositoryUrl is not null && string.Equals(repositoryUrl, ownRepositoryUrl, StringComparison.OrdinalIgnoreCase);
            var hasMismatch = isOwn && members
                .Select(member => GetShortCommit(member.Commit))
                .Where(commit => commit is not null)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1;

            var inventoryAssemblies = members
                .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.LoadContext != DefaultLoadContextName)
                .ThenBy(member => member.LoadContext, StringComparer.OrdinalIgnoreCase)
                .Select(member => new InventoryAssembly(
                    GetKey(member), member.Name, member.LoadContext, member.Version, member.Commit, mismatchedNames.Contains(member.Name)))
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
        for (var first = 0; first < assemblies.Count; first++)
        {
            for (var second = first + 1; second < assemblies.Count; second++)
            {
                if (string.Equals(assemblies[first].Name, assemblies[second].Name, StringComparison.OrdinalIgnoreCase) &&
                    IsDifferentBuild(assemblies[first], assemblies[second]))
                {
                    names.Add(assemblies[first].Name);
                }
            }
        }

        return names;
    }

    private static bool IsDifferentBuild(LoadedAssembly first, LoadedAssembly second) =>
        !string.Equals(first.Version, second.Version, StringComparison.OrdinalIgnoreCase) ||
        (GetShortCommit(first.Commit) is { } firstCommit &&
         GetShortCommit(second.Commit) is { } secondCommit &&
         firstCommit != secondCommit);

    private static string? GetShortCommit(string? commit) =>
        commit is { Length: >= ShortCommitLength } ? commit[..ShortCommitLength].ToLowerInvariant() : null;

    private static string GetKey(LoadedAssembly assembly) =>
        assembly.LoadContext == DefaultLoadContextName ? assembly.Name : $"{assembly.Name} ({assembly.LoadContext})";

    private static string GetUniqueName(string? repositoryUrl, HashSet<string> usedNames)
    {
        var name = repositoryUrl is null ? UnknownRepositoryName : repositoryUrl[(repositoryUrl.LastIndexOf('/') + 1)..];
        var candidate = name;
        var suffix = 2;
        while (!usedNames.Add(candidate))
        {
            candidate = $"{name} {suffix++}";
        }

        return candidate;
    }
}
