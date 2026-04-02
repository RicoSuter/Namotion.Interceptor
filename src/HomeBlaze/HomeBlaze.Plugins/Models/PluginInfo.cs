using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Plugins.Models;

[InterceptorSubject]
public partial class PluginInfo : ITitleProvider
{
    private readonly PluginManager _manager;

    public PluginInfo(PluginManager manager)
    {
        _manager = manager;
        Name = "";
        Version = "";
        Status = "";
        AssemblyCount = 0;
    }
    
    [Derived]
    public string? Title => Name + " v" + Version;

    [State]
    public partial string Name { get; internal set; }

    [State]
    public partial string Version { get; internal set; }

    [State]
    public partial int AssemblyCount { get; internal set; }

    [State]
    public partial string Status { get; internal set; }

    [Operation(Title = "Remove Plugin", RequiresConfirmation = true)]
    public void RemovePlugin(string packageName)
    {
        _manager.RemovePlugin(packageName);
    }
}
