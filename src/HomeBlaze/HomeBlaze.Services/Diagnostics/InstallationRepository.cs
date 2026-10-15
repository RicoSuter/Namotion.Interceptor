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
    public string? IconColor => HasMismatch || Assemblies.Values.Any(assembly => assembly.HasMismatch) ? "Warning" : null;

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
        var assemblies = new Dictionary<string, InstallationAssembly>(entry.Assemblies.Length, StringComparer.Ordinal);
        var isChanged = entry.Assemblies.Length != current.Count;
        foreach (var item in entry.Assemblies)
        {
            if (current.TryGetValue(item.Key, out var assembly))
            {
                assembly.HasMismatch = item.HasMismatch;
            }
            else
            {
                assembly = new InstallationAssembly(item);
                isChanged = true;
            }

            assemblies[item.Key] = assembly;
        }

        if (isChanged)
        {
            Assemblies = assemblies;
        }
    }
}
