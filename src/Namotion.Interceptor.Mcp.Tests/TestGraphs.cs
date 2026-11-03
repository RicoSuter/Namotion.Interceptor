using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Mcp.Tests;

/// <summary>
/// Contexts and subject graphs that several test classes share.
/// </summary>
internal static class TestGraphs
{
    public static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    /// <summary>
    /// A house whose Private and then Public hold the same device, so without a policy the device's path goes through
    /// Private.
    /// </summary>
    public static (TestHouse House, TestDevice Device) CreateHouseWithSharedDevice(IInterceptorSubjectContext? context = null)
    {
        context ??= CreateContext();
        var house = new TestHouse(context) { Address = "Main Street" };
        var device = new TestDevice(context) { DeviceName = "Camera" };
        house.Private = device;
        house.Public = device;
        return (house, device);
    }
}
