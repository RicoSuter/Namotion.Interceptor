using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.OpcUa.Client;
using Namotion.Interceptor.OpcUa.Server;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.OpcUa.Tests;

public partial class OpcUaRegistrationTests
{
    [InterceptorSubject]
    public partial class RegistrationTestSubject
    {
        public partial string Name { get; set; }
    }

    private static RegistrationTestSubject CreateSubject()
    {
        // OpcUaSubjectClientSource requires WithLifecycle for cleanup on detach.
        var context = InterceptorSubjectContext.Create().WithLifecycle();
        return new RegistrationTestSubject(context);
    }

    [Fact]
    public void WhenClientRegistrationIsResolved_ThenReturnsKeyedClientSource()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());

        var registration = services.AddOpcUaSubjectClientSource(
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            sp => new OpcUaClientConfiguration
            {
                ServerUrl = "opc.tcp://localhost:4840",
                TypeResolver = new OpcUaTypeResolver(NullLogger<OpcUaTypeResolver>.Instance),
                ValueConverter = new OpcUaValueConverter(),
                SubjectFactory = new OpcUaSubjectFactory(DefaultSubjectFactory.Instance)
            });

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = registration.Resolve(serviceProvider);

        // Assert - resolves the same instance the keyed lookup returns,
        // and that singleton is the concrete OpcUaSubjectClientSource (no double registration).
        Assert.NotNull(resolved);
        Assert.IsType<OpcUaSubjectClientSource>(resolved);
        Assert.Same(resolved, registration.Resolve(serviceProvider));
    }

    [Fact]
    public void WhenServerRegistrationIsResolved_ThenReturnsKeyedServer()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => CreateSubject());

        var registration = services.AddOpcUaSubjectServer(
            sp => sp.GetRequiredService<RegistrationTestSubject>(),
            sp => new OpcUaServerConfiguration
            {
                ValueConverter = new OpcUaValueConverter()
            });

        // Act
        using var serviceProvider = services.BuildServiceProvider();
        var resolved = registration.Resolve(serviceProvider);

        // Assert - same instance returned from keyed lookup, single registration.
        Assert.NotNull(resolved);
        Assert.Same(resolved, registration.Resolve(serviceProvider));
    }

    [Fact]
    public void WhenTwoServersAreRegisteredForSameSubject_ThenOpcUaVariableKeysDiffer()
    {
        // Arrange - regression for multi-server collision: two servers exposing the
        // same subject must use distinct per-instance keys so the BaseDataVariableState
        // attached by one server cannot overwrite the other's reference.
        var subject = CreateSubject();
        var configuration = new OpcUaServerConfiguration { ValueConverter = new OpcUaValueConverter() };
        var logger = NullLogger<OpcUaSubjectServerBackgroundService>.Instance;

        // Act
        var first = new OpcUaSubjectServerBackgroundService(subject, configuration, logger);
        var second = new OpcUaSubjectServerBackgroundService(subject, configuration, logger);

        // Assert
        Assert.NotEqual(first.OpcUaVariableKey, second.OpcUaVariableKey);
        Assert.StartsWith("OpcUaVariable:", first.OpcUaVariableKey);
        Assert.StartsWith("OpcUaVariable:", second.OpcUaVariableKey);
    }
}
