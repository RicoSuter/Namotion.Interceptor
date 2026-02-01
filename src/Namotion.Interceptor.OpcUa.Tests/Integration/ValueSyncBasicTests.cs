using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for basic read/write synchronization using the shared OPC UA server.
/// </summary>
[Trait("Category", "Integration")]
public class ValueSyncBasicTests : SharedServerTestBase
{
    public ValueSyncBasicTests(
        SharedOpcUaServerFixture serverFixture,
        ITestOutputHelper output)
        : base(serverFixture, output) { }

    [Fact]
    public async Task WriteAndReadPrimitives_ShouldUpdateClient()
    {
        // Arrange - use dedicated test area
        var serverArea = ServerFixture.ServerRoot.ReadWrite.BasicSync;
        var clientArea = Client!.Root!.ReadWrite.BasicSync;

        // Act & Assert - Test string property from server
        serverArea.Name = "Updated Server Name";
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Name == "Updated Server Name",
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive server's name update");

        // Test string property from client
        clientArea.Name = "Updated Client Name";
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Name == "Updated Client Name",
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's name update");

        // Test numeric property from server
        serverArea.Number = 123.45m;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.Number == 123.45m,
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive server's number update");

        // Test numeric property from client
        clientArea.Number = 54.321m;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.Number == 54.321m,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's number update");
    }

    [Fact]
    public async Task WriteAndReadArraysOnServer_ShouldUpdateClient()
    {
        // Arrange - use dedicated test area
        var serverArea = ServerFixture.ServerRoot.ReadWrite.ArraySync;
        var clientArea = Client!.Root!.ReadWrite.ArraySync;

        Logger.Log($"Server initial ScalarNumbers: [{string.Join(", ", serverArea.ScalarNumbers)}]");
        Logger.Log($"Client initial ScalarNumbers: [{string.Join(", ", clientArea.ScalarNumbers)}]");

        // Act - update server arrays
        var newNumbers = new[] { 100, 200, 300 };
        serverArea.ScalarNumbers = newNumbers;
        Logger.Log($"Server updated ScalarNumbers: [{string.Join(", ", serverArea.ScalarNumbers)}]");

        // Assert - wait for array synchronization
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.ScalarNumbers.SequenceEqual(newNumbers),
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive server's array update");

        Logger.Log($"Client ScalarNumbers after update: [{string.Join(", ", clientArea.ScalarNumbers)}]");
        Assert.Equal(newNumbers, clientArea.ScalarNumbers);
    }
}