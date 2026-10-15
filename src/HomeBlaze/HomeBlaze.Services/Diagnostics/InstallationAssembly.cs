using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// An assembly loaded by the installation. Only the owning <c>HomeBlazeInstallation</c> writes its values.
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

    [Derived]
    public string? IconColor => HasMismatch ? "Warning" : null;

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

    /// <summary>
    /// Whether the same assembly is loaded elsewhere with another version or commit.
    /// </summary>
    [State(IsDiscrete = true, Position = 404)]
    public partial bool HasMismatch { get; internal set; }

    internal InstallationAssembly(InventoryAssembly entry)
    {
        _title = entry.Key;
        Name = entry.Name;
        Version = entry.Version;
        Commit = entry.Commit;
        LoadContext = entry.LoadContext;
        HasMismatch = entry.HasMismatch;
    }
}
