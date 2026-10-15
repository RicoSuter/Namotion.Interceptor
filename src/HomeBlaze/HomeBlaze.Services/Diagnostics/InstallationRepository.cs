using System.Collections.Immutable;
using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// The assemblies of the installation that come from one source repository. Only the owning
/// <c>HomeBlazeInstallation</c> writes its values.
/// </summary>
[DisplayName("Repository")]
[InterceptorSubject]
public partial class InstallationRepository : ITitleProvider, IIconProvider
{
    [Derived]
    public string? Title => Name;

    [Derived]
    public string IconName => "Source";

    [Derived]
    public string? IconColor
    {
        get
        {
            if (HasMismatch)
            {
                return "Warning";
            }

            foreach (var assembly in Assemblies.Values)
            {
                if (assembly.HasMismatch)
                {
                    return "Warning";
                }
            }

            return null;
        }
    }

    [State(Position = 400)]
    public partial string Name { get; internal set; }

    [State(Position = 401)]
    public partial Uri? RepositoryUrl { get; internal set; }

    /// <summary>
    /// Whether assemblies of the repository HomeBlaze itself was built from come from more than one commit.
    /// </summary>
    [State(IsDiscrete = true, Position = 402)]
    public partial bool HasMismatch { get; internal set; }

    [State(Position = 410)]
    public partial Dictionary<string, InstallationAssembly> Assemblies { get; internal set; }

    internal InstallationRepository(string name, string? repositoryUrl)
    {
        Name = name;
        RepositoryUrl = Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri) ? uri : null;
        HasMismatch = false;
        Assemblies = new Dictionary<string, InstallationAssembly>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Applies an inventory entry. Assemblies that are still loaded keep their subject, so their paths and history stay.
    /// </summary>
    internal void Apply(InventoryRepository entry)
    {
        HasMismatch = entry.HasMismatch;

        var current = Assemblies;
        if (HasSameKeys(current, entry.Assemblies))
        {
            // No key change: update the existing subjects in place instead of allocating a replacement dictionary.
            foreach (var item in entry.Assemblies)
            {
                current[item.Key].Apply(item);
            }

            return;
        }

        var assemblies = new Dictionary<string, InstallationAssembly>(entry.Assemblies.Length, StringComparer.Ordinal);
        foreach (var item in entry.Assemblies)
        {
            if (current.TryGetValue(item.Key, out var assembly))
            {
                assembly.Apply(item);
            }
            else
            {
                assembly = new InstallationAssembly(item);
            }

            assemblies[item.Key] = assembly;
        }

        // The published dictionary is read concurrently, so it is replaced, never mutated.
        Assemblies = assemblies;
    }

    private static bool HasSameKeys(Dictionary<string, InstallationAssembly> current, ImmutableArray<InventoryAssembly> entries)
    {
        if (current.Count != entries.Length)
        {
            return false;
        }

        foreach (var item in entries)
        {
            if (!current.ContainsKey(item.Key))
            {
                return false;
            }
        }

        return true;
    }
}
