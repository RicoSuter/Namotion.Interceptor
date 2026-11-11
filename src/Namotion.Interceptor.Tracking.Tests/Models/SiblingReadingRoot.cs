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
/// Declares the device ahead of the configuration it reads, so that the device attaches first.
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
    public int EvaluationCount { get; private set; }

    [Derived]
    public bool IsInstalled
    {
        get
        {
            EvaluationCount++;
            return this.TryGetFirstParent<SiblingReadingMachine>()?.Configuration.Installed ?? false;
        }
    }
}
