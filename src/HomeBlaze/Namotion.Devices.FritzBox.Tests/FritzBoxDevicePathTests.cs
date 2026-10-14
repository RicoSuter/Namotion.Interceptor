using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.FritzBox.Tests.Testing;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace Namotion.Devices.FritzBox.Tests;

public class FritzBoxDevicePathTests
{
    [Fact]
    public void WhenADeviceSubjectMatchesAClient_ThenTheClientHoldsTheCanonicalPathOfTheDevice()
    {
        // Arrange
        var context = CreateContext();
        var device = new TestNetworkDevice(Snapshots.Laptop);
        var router = new FritzBox(NullLogger<FritzBox>.Instance);
        context.AddService<ISubjectPathResolver>(new FakePathResolver(device, "/Devices/Laptop.json"));
        Attach(context, router, device);
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);

        // Act
        router.ApplyDevicePaths();

        // Assert
        Assert.Equal("/Devices/Laptop.json", router.Clients[Snapshots.Laptop].DevicePath);
        Assert.Null(router.Clients[Snapshots.Desktop].DevicePath);
    }

    [Fact]
    public void WhenTheDeviceSubjectNoLongerMatches_ThenThePathIsCleared()
    {
        // Arrange
        var context = CreateContext();
        var device = new TestNetworkDevice(Snapshots.Laptop);
        var router = new FritzBox(NullLogger<FritzBox>.Instance);
        context.AddService<ISubjectPathResolver>(new FakePathResolver(device, "/Devices/Laptop.json"));
        Attach(context, router, device);
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);
        router.ApplyDevicePaths();

        // Act
        device.MacAddress = "AA:AA:AA:00:00:09";
        router.ApplyDevicePaths();

        // Assert
        Assert.Null(router.Clients[Snapshots.Laptop].DevicePath);
    }

    [Fact]
    public void WhenNoPathResolverIsRegistered_ThenNoPathIsSet()
    {
        // Arrange
        var context = CreateContext();
        var device = new TestNetworkDevice(Snapshots.Laptop);
        var router = new FritzBox(NullLogger<FritzBox>.Instance);
        Attach(context, router, device);
        router.ApplySnapshot(Snapshots.Create(), router.WlanCommandGeneration);

        // Act
        router.ApplyDevicePaths();

        // Assert
        Assert.All(router.Clients.Values, client => Assert.Null(client.DevicePath));
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

    private static void Attach(IInterceptorSubjectContext context, params IInterceptorSubject[] subjects)
    {
        foreach (var subject in subjects)
        {
            subject.Context.AddFallbackContext(context);
        }

        Assert.All(subjects, subject => Assert.NotNull(subject.TryGetRegisteredSubject()));
    }

    private sealed class FakePathResolver(IInterceptorSubject device, string devicePath) : ISubjectPathResolver
    {
        public string? GetPath(IInterceptorSubject subject, PathStyle style) => ReferenceEquals(subject, device) ? devicePath : null;

        public IReadOnlyList<string> GetPaths(IInterceptorSubject subject, PathStyle style) =>
            GetPath(subject, style) is { } path ? [path] : [];

        public IInterceptorSubject? ResolveSubject(string path, PathStyle style, IInterceptorSubject? relativeTo = null) =>
            path == devicePath ? device : null;
    }
}
