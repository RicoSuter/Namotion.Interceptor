using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class SiblingReadingRoot
{
    public SiblingReadingRoot()
    {
        Machines = [];
    }

    public partial SiblingReadingMachine? Machine { get; set; }

    public partial SiblingReadingMachine[] Machines { get; set; }
}

/// <summary>
/// The tests rely on the device attaching before the configuration it reads.
/// </summary>
[InterceptorSubject]
public partial class SiblingReadingMachine
{
    public SiblingReadingMachine()
    {
        Device = new SiblingReadingDevice();
        Configuration = new SiblingReadingConfiguration();
    }

    public partial SiblingReadingDevice Device { get; set; }

    public partial SiblingReadingConfiguration Configuration { get; set; }
}

[InterceptorSubject]
public partial class SiblingReadingConfiguration
{
    public partial bool Installed { get; set; }
}

[InterceptorSubject]
public partial class SiblingReadingDevice
{
    [Derived]
    public bool IsInstalled => ReadInstalled();

    /// <summary>
    /// A new array on every evaluation, whose item is an array of rank two.
    /// </summary>
    [Derived]
    public object[] InstalledGrids => [new[,] { { ReadInstalled() } }];

    private bool ReadInstalled()
    {
        return this.TryGetFirstParent<SiblingReadingMachine>()?.Configuration.Installed ?? false;
    }
}
