using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// An assembly loaded by the installation. Only the owning <see cref="HomeBlazeInstallation"/> writes its values.
/// </summary>
[DisplayName("Assembly")]
[InterceptorSubject]
public partial class InstallationAssembly : ITitleProvider, IIconProvider
{
    private readonly string _title;

    [Derived]
    public string? Title => _title;

    [Derived]
    public string IconName => "Code";

    [State(Position = 400)]
    public partial string Name { get; internal set; }

    [State(Position = 401)]
    public partial string Version { get; internal set; }

    [State(Position = 402)]
    public partial string? Commit { get; internal set; }

    /// <summary>
    /// <c>Default</c>, or the name of the plugin load context the assembly was loaded into.
    /// </summary>
    [State(Position = 403)]
    public partial string LoadContext { get; internal set; }

    internal InstallationAssembly(InventoryAssembly entry)
    {
        _title = entry.Key;
        Apply(entry);
    }

    /// <summary>
    /// Refreshes the values of a reused instance from a newly built inventory entry with the same key.
    /// </summary>
    internal void Apply(InventoryAssembly entry)
    {
        Name = entry.Name;
        Version = entry.Version;
        Commit = entry.Commit;
        LoadContext = entry.LoadContext;
    }
}
