using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Plugins.Models;

[InterceptorSubject]
public partial class PluginInfo : ITitleProvider, IIconProvider, IMonitoredService
{
    private readonly PluginManager _manager;

    public PluginInfo(PluginManager manager)
    {
        _manager = manager;
        Name = "";
        Version = "";
        Description = "";
        Authors = "";
        IconUrl = null;
        Tags = [];
        HostDependencies = [];
        PrivateDependencies = [];
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
        Assemblies = [];
    }

    [Derived]
    public string Title => string.IsNullOrEmpty(Version) ? Name : $"{Name} v{Version}";

    [Derived]
    public string? IconName => Status == ServiceStatus.Error ? "ErrorOutline" : "Extension";

    [Derived]
    public string? IconColor =>
        Status == ServiceStatus.Running ? "Success" :
        Status == ServiceStatus.Error ? "Error" : "Default";

    [State]
    public partial string Name { get; internal set; }

    [State]
    public partial string Version { get; internal set; }

    [State]
    public partial string Description { get; internal set; }

    [State]
    public partial string Authors { get; internal set; }

    [State]
    public partial string? IconUrl { get; internal set; }

    [State]
    public partial string[] Tags { get; internal set; }

    [State]
    public partial string[] HostDependencies { get; internal set; }

    [State]
    public partial string[] PrivateDependencies { get; internal set; }

    [State]
    public partial string[] Assemblies { get; internal set; }

    [State]
    public partial ServiceStatus Status { get; internal set; }

    [State]
    public partial string? StatusMessage { get; internal set; }

    [Operation(Title = "Remove Plugin", RequiresConfirmation = true)]
    public void RemovePlugin()
    {
        _manager.RemovePlugin(Name);
    }
}
