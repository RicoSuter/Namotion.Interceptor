using System.Reflection;
using Xunit;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Connectors;
using Namotion.Interceptor.ConnectorTester.Model;

namespace Namotion.Interceptor.ConnectorTester.Tests.Configuration;

public class ConnectorTesterConfigurationTests
{
    // Mirrors what RandomValueMutationStrategy and BatchValueMutationStrategy actually index into
    // (participantIndex % property count) when DisjointProperties assigns one property per
    // participant, so the expected limit tracks the model instead of a number assumed by the test.
    private static readonly IReadOnlySet<Type> MutableValuePropertyTypes =
        new HashSet<Type> { typeof(string), typeof(decimal), typeof(int), typeof(long) };

    private static int MutablePropertyCount => typeof(TestNode)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Count(property => MutableValuePropertyTypes.Contains(property.PropertyType));

    private static ConnectorTesterConfiguration CreateConfigurationWithParticipants(int participantCount)
        => new()
        {
            DisjointProperties = true,
            Clients = Enumerable.Range(0, participantCount - 1)
                .Select(index => new ParticipantConfiguration { Name = $"client-{index}" })
                .ToList()
        };

    [Fact]
    public void WhenDisjointParticipantCountIsAtTheModelsPropertyCount_ThenValidateDoesNotThrow()
    {
        // Arrange
        var configuration = CreateConfigurationWithParticipants(MutablePropertyCount);

        // Act & Assert
        var exception = Record.Exception(configuration.ValidateDisjointProperties);
        Assert.Null(exception);
    }

    [Fact]
    public void WhenDisjointParticipantCountExceedsTheModelsPropertyCount_ThenValidateThrows()
    {
        // Arrange
        var configuration = CreateConfigurationWithParticipants(MutablePropertyCount + 1);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(configuration.ValidateDisjointProperties);
    }

    [Theory]
    [InlineData("opcua",     ConnectorKind.OpcUa)]
    [InlineData("OpcUa",     ConnectorKind.OpcUa)]
    [InlineData("OPCUA",     ConnectorKind.OpcUa)]
    [InlineData("mqtt",      ConnectorKind.Mqtt)]
    [InlineData("MQTT",      ConnectorKind.Mqtt)]
    [InlineData("websocket", ConnectorKind.WebSocket)]
    [InlineData("WebSocket", ConnectorKind.WebSocket)]
    public void WhenConnectorStringValid_ThenConnectorKindParses(string connectorString, ConnectorKind expectedKind)
    {
        // Arrange
        var configuration = new ConnectorTesterConfiguration { Connector = connectorString };

        // Act
        var kind = configuration.ConnectorKind;

        // Assert
        Assert.Equal(expectedKind, kind);
    }

    [Fact]
    public void WhenConnectorStringInvalid_ThenConnectorKindThrows()
    {
        // Arrange
        var configuration = new ConnectorTesterConfiguration { Connector = "not-a-connector" };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _ = configuration.ConnectorKind);
    }
}
