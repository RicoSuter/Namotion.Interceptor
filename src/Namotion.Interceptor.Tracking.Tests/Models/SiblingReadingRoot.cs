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
/// The tests rely on the device attaching before the configuration it reads, which they check through the number of
/// evaluations of the device.
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

    /// <summary>
    /// A new list on every evaluation, of a subject that is attached before this property.
    /// </summary>
    [Derived]
    public List<bool> InstalledStateList => [Configuration.Installed];
}

[InterceptorSubject]
public partial class SiblingReadingConfiguration
{
    public partial bool Installed { get; set; }

    public int EvaluationCount { get; private set; }

    [Derived]
    public string State
    {
        get
        {
            EvaluationCount++;
            return Installed ? "Installed" : "NotInstalled";
        }
    }
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
            return ReadInstalled();
        }
    }

    /// <summary>
    /// A new array on every evaluation, of a subject that is attached after this property.
    /// </summary>
    [Derived]
    public bool[] InstalledStates => [ReadInstalled()];

    private bool ReadInstalled()
    {
        return this.TryGetFirstParent<SiblingReadingMachine>()?.Configuration.Installed ?? false;
    }
}
