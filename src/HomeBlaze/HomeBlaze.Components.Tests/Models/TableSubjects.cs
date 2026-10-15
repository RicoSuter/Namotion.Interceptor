using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Components.Tests.Models;

public interface ITestDevice
{
    int Speed { get; }
}

public interface ITestSensor<T>;

public abstract class TestDeviceBase;

[InterceptorSubject]
public partial class TestDevice : TestDeviceBase, ITestDevice, ITestSensor<int>, ITitleProvider
{
    public TestDevice()
    {
        Name = string.Empty;
    }

    [State]
    public partial string Name { get; set; }

    [State("Rotation speed")]
    public partial int Speed { get; set; }

    public string? Title => Name;
}

[InterceptorSubject]
public partial class TestFolder
{
    public TestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
        Devices = [];
        DevicesByName = new Dictionary<string, TestDevice>();
    }

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public partial TestDevice[] Devices { get; set; }

    public partial Dictionary<string, TestDevice> DevicesByName { get; set; }

    public partial TestDevice? Main { get; set; }

    public partial string? Label { get; set; }
}
