using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using Namotion.Interceptor.Connectors;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors;
using Namotion.Interceptor.ConnectorTester.Connectors.OpcUa;
using Namotion.Interceptor.ConnectorTester.Connectors.Mqtt;
using Namotion.Interceptor.ConnectorTester.Connectors.WebSocket;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Connectors;

public class ConnectorBindingsRegistrationTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    [Theory]
    [MemberData(nameof(AllBindings))]
    public void WhenRegisterServerCalled_ThenAtLeastOneSubjectConnectorRegistered(IConnectorBindings bindings)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var root = new TestNode(CreateContext());

        // Act
        bindings.RegisterServer(services, root, bindings.DefaultPort);
        var provider = services.BuildServiceProvider();
        var connectors = provider.GetServices<IHostedService>().OfType<ISubjectConnector>().ToList();

        // Assert
        Assert.NotEmpty(connectors);
    }

    [Theory]
    [MemberData(nameof(AllBindings))]
    public void WhenRegisterClientCalled_ThenAtLeastOneSubjectConnectorRegistered(IConnectorBindings bindings)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        var root = new TestNode(CreateContext());
        var participantConfiguration = new ParticipantConfiguration { Name = "client-test" };

        // Act
        bindings.RegisterClient(services, root, participantConfiguration, bindings.DefaultPort);
        var provider = services.BuildServiceProvider();
        var connectors = provider.GetServices<IHostedService>().OfType<ISubjectConnector>().ToList();

        // Assert
        Assert.NotEmpty(connectors);
    }

    public static IEnumerable<object[]> AllBindings()
    {
        yield return [new OpcUaConnectorBindings()];
        yield return [new MqttConnectorBindings()];
        yield return [new WebSocketConnectorBindings()];
    }
}
