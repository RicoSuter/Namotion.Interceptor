using System.Collections.Immutable;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// An assembly loaded in the process. <see cref="RepositoryUrl"/> is normalized by <see cref="BuildInformation"/>.
/// </summary>
internal readonly record struct LoadedAssembly(string Name, string LoadContext, string Version, string? Commit, string? RepositoryUrl);

internal sealed record InventoryAssembly(string Key, string Name, string LoadContext, string Version, string? Commit);

internal sealed record InventoryRepository(string Name, string? RepositoryUrl, ImmutableArray<InventoryAssembly> Assemblies);

/// <summary>
/// Groups loaded assemblies by repository.
/// </summary>
internal static class AssemblyInventory
{
    public const string DefaultLoadContextName = "Default";
    public const string UnknownRepositoryName = "Unknown";

    public static ImmutableArray<InventoryRepository> Create(IReadOnlyList<LoadedAssembly> assemblies)
    {
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
            var inventoryAssemblies = members
                .OrderBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.LoadContext != DefaultLoadContextName)
                .ThenBy(member => member.LoadContext, StringComparer.OrdinalIgnoreCase)
                .Select(member => new InventoryAssembly(
                    GetUniqueKey(member, usedKeys), member.Name, member.LoadContext, member.Version, member.Commit))
                .ToImmutableArray();

            repositories.Add(new InventoryRepository(GetUniqueName(repositoryUrl, usedNames), repositoryUrl, inventoryAssemblies));
        }

        return repositories
            .OrderBy(repository => repository.RepositoryUrl is null)
            .ThenBy(repository => repository.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

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
