using Namotion.Interceptor.Testing;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Integration;

[Collection(MatterServerCollection.Name)]
[Trait("Category", "Integration")]
public class MatterServerIntegrationTests(MatterServerFixture fixture)
{
    private static readonly AttributePath LightOnOff = new(1, 6, 0);
    private static readonly AttributePath LightOnTime = new(1, 6, 0x4001);

    [Fact]
    public async Task WhenConnectingOverUnixSocket_ThenServerInfoIsReceived()
    {
        // Act
        await using var controller = await MatterServerController.ConnectAsync(fixture.UnixUrl);

        // Assert
        Assert.True(controller.ServerInfo.SchemaVersion >= MatterServerController.MinimumServerSchemaVersion);
    }

    [Fact]
    public async Task WhenCommissioningVirtualLight_ThenItCanBeControlledAndRemoved()
    {
        // Arrange
        await using var controller = await MatterServerController.ConnectAsync(fixture.TcpUrl);
        await controller.StartListeningAsync();

        // Act: commission
        var payloads = await controller.ParseOnboardingPayloadAsync(MatterServerFixture.LightManualCode);
        // Commissioning has no request timeout, so the test bounds it.
        using var commissioningTimeout = new CancellationTokenSource(TestTimeouts.Default);
        var node = await controller.CommissionAsync(MatterServerFixture.LightManualCode, networkOnly: true, commissioningTimeout.Token);

        // Assert: structure
        Assert.Equal(20202021u, Assert.Single(payloads).SetupPinCode);
        Assert.Equal("Namotion", node.Attributes[new AttributePath(0, 40, 1)].GetString());
        var deviceTypes = node.Attributes[new AttributePath(1, 29, 0)];
        var hasOnOffLight = false;
        foreach (var deviceType in deviceTypes.EnumerateList())
        {
            hasOnOffLight |= deviceType.TryGetField(0, "deviceType", out var id) && id.GetUInt64() == 0x0100;
        }

        Assert.True(hasOnOffLight, $"Device types: {deviceTypes}");

        // Act: an unknown command name
        var unknownCommand = await Assert.ThrowsAsync<MatterException>(() => controller.InvokeAsync(node.NodeId, new CommandPath(1, 6, 2), "noSuchCommand"));

        // Assert
        Assert.Equal(MatterErrorCode.InvalidArguments, unknownCommand.ErrorCode);

        // Act: toggle, which the light confirms with an attribute report. The node from commissioning can lack values
        // until the device's first report, which then arrives as a node update.
        var wasOn = node.Attributes.TryGetValue(LightOnOff, out var onOff)
            ? onOff.GetBoolean()
            : (await WaitForAttributeAsync(controller, node.NodeId, LightOnOff, _ => true)).GetBoolean();
        await controller.InvokeAsync(node.NodeId, new CommandPath(1, 6, 2), "toggle");

        // Assert
        await WaitForAttributeAsync(controller, node.NodeId, LightOnOff, value => value.GetBoolean() != wasOn);

        // Act: write a writable attribute
        var writeError = await Record.ExceptionAsync(() =>
            controller.WriteAttributeAsync(node.NodeId, LightOnTime, MatterPayload.Create(writer => writer.WriteUInt64(10))));

        // Assert
        Assert.Null(writeError);

        // Act: remove
        await controller.RemoveNodeAsync(node.NodeId);

        // Assert
        await WaitForEventAsync(controller, controllerEvent => controllerEvent is MatterNodeRemovedEvent removed && removed.NodeId == node.NodeId);
    }

    /// <summary>Waits for a value of the attribute, from an attribute change or from a node update that carries it.</summary>
    private static async Task<MatterValue> WaitForAttributeAsync(IMatterController controller, ulong nodeId, AttributePath path, Func<MatterValue, bool> condition)
    {
        MatterValue value = default;
        await WaitForEventAsync(controller, controllerEvent =>
        {
            switch (controllerEvent)
            {
                case MatterAttributeChangedEvent changed when changed.NodeId == nodeId && changed.Path == path:
                    value = changed.Value;
                    return condition(value);

                case MatterNodeUpdatedEvent updated when updated.Node.NodeId == nodeId && updated.Node.Attributes.TryGetValue(path, out value):
                    return condition(value);

                default:
                    return false;
            }
        });

        return value;
    }

    private static async Task WaitForEventAsync(IMatterController controller, Func<MatterControllerEvent, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Default);
        await foreach (var controllerEvent in controller.Events.ReadAllAsync(timeout.Token))
        {
            if (condition(controllerEvent))
            {
                return;
            }
        }

        throw new InvalidOperationException("The event channel completed before the expected event arrived.");
    }
}
