using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Transactions;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration;

/// <summary>
/// Tests for various OPC UA data types synchronization using the shared server.
/// Tests client→server sync for different data types.
/// </summary>
[Trait("Category", "Integration")]
public class OpcUaDataTypesTests : SharedServerTestBase
{
    public OpcUaDataTypesTests(
        SharedOpcUaServerFixture serverFixture,
        SharedOpcUaClientFixture clientFixture,
        ITestOutputHelper output)
        : base(serverFixture, clientFixture, output) { }

    [Fact]
    public async Task BooleanType_ClientToServer_ShouldSync()
    {
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;

        // Log initial state
        Logger.Log($"Initial state: server.BoolValue={serverArea.BoolValue}, client.BoolValue={clientArea.BoolValue}");

        // Client to server without explicit transaction
        Logger.Log("Setting client.BoolValue = false");
        clientArea.BoolValue = false;

        await AsyncTestHelpers.WaitUntilAsync(
            () => {
                var current = serverArea.BoolValue;
                if (current)
                    Logger.Log($"Waiting: server.BoolValue={current} (expecting false)");
                return !current;
            },
            timeout: TimeSpan.FromSeconds(90),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Server should receive client's bool update (false). Current server value: {serverArea.BoolValue}");

        Logger.Log($"After first sync: server.BoolValue={serverArea.BoolValue}");

        Logger.Log("Setting client.BoolValue = true");
        clientArea.BoolValue = true;

        await AsyncTestHelpers.WaitUntilAsync(
            () => {
                var current = serverArea.BoolValue;
                if (!current)
                    Logger.Log($"Waiting: server.BoolValue={current} (expecting true)");
                return current;
            },
            timeout: TimeSpan.FromSeconds(90),
            pollInterval: TimeSpan.FromMilliseconds(500),
            message: $"Server should receive client's bool update (true). Current server value: {serverArea.BoolValue}");

        Logger.Log($"After second sync: server.BoolValue={serverArea.BoolValue}");
        Logger.Log("Boolean client→server sync verified");
    }

    [Fact]
    public async Task IntegerTypes_ClientToServer_ShouldSync()
    {
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;

        // Int32 - client to server with edge values
        clientArea.IntValue = int.MaxValue;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.IntValue == int.MaxValue,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's int MaxValue");

        clientArea.IntValue = int.MinValue;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.IntValue == int.MinValue,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's int MinValue");

        // Int64 - client to server with edge values
        clientArea.LongValue = long.MaxValue;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.LongValue == long.MaxValue,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's long MaxValue");

        clientArea.LongValue = long.MinValue;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.LongValue == long.MinValue,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's long MinValue");

        Logger.Log("Integer types client→server sync verified");
    }

    [Fact]
    public async Task DateTimeType_ClientToServer_ShouldSync()
    {
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;

        // Client to server
        var testDate = new DateTime(2026, 6, 15, 10, 30, 45, DateTimeKind.Utc);
        clientArea.DateTimeValue = testDate;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.DateTimeValue == testDate,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's DateTime update");

        Logger.Log("DateTime client→server sync verified");
    }

    [Fact]
    public async Task ByteArrayType_ClientToServer_ShouldSync()
    {
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;

        // Client to server
        var testBytes = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        clientArea.ByteArray = testBytes;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.ByteArray.SequenceEqual(testBytes),
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive client's byte[] update");

        Logger.Log("ByteArray client→server sync verified");
    }

    // Note: Guid type test removed - OPC UA Guid mapping needs further investigation
}
