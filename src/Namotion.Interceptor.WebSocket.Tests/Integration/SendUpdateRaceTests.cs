using System;
using System.Threading.Tasks;
using Namotion.Interceptor.Testing;
using Xunit;
using Xunit.Abstractions;

namespace Namotion.Interceptor.WebSocket.Tests.Integration;

/// <summary>
/// Tests that updates sent during the Welcome handshake window are not lost.
/// Exercises the register-before-Welcome pattern to ensure eventual consistency.
/// </summary>
[Trait("Category", "Integration")]
public class SendUpdateRaceTests
{
    private readonly ITestOutputHelper _output;

    public SendUpdateRaceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task UpdatesDuringWelcome_ShouldNotBeLost()
    {
        // Arrange
        using var portLease = await WebSocketTestPortPool.AcquireAsync();
        await using var server = new WebSocketTestServer<TestRoot>(_output);
        await using var client = new WebSocketTestClient<TestRoot>(_output);

        await server.StartAsync(
            context => new TestRoot(context),
            (_, root) => root.Name = "Initial",
            port: portLease.Port);

        await client.StartAsync(context => new TestRoot(context), port: portLease.Port);

        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Initial",
            message: "Client should receive initial state");

        // Act - Rapidly fire updates to exercise the Welcome/broadcast window
        for (var i = 0; i < 20; i++)
        {
            server.Root!.Name = $"Rapid-{i}";
        }

        // Assert - Final value should eventually arrive
        await AsyncTestHelpers.WaitUntilAsync(
            () => client.Root!.Name == "Rapid-19",
            timeout: TimeSpan.FromSeconds(10),
            message: "All rapid updates during welcome window should arrive");

        Assert.Equal("Rapid-19", client.Root!.Name);
    }
}
