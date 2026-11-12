using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
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
            message: () => $"Server should receive client's bool update (false). Current server value: {serverArea.BoolValue}");

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
            message: () => $"Server should receive client's bool update (true). Current server value: {serverArea.BoolValue}");

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

    [Fact]
    public async Task GuidType_ShouldSyncBothDirections()
    {
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;

        // Client to server
        var clientGuid = Guid.NewGuid();
        clientArea.GuidValue = clientGuid;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.GuidValue == clientGuid,
            timeout: TimeSpan.FromSeconds(90),
            message: () => $"Server should receive client's Guid update. Expected: {clientGuid}, Actual: {serverArea.GuidValue}");

        // Server to client
        var serverGuid = Guid.NewGuid();
        serverArea.GuidValue = serverGuid;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.GuidValue == serverGuid,
            timeout: TimeSpan.FromSeconds(90),
            message: () => $"Client should receive server's Guid update. Expected: {serverGuid}, Actual: {clientArea.GuidValue}");

        Logger.Log("Guid bidirectional sync verified");
    }

    [Fact]
    public async Task NullableGuidType_ShouldSyncBothDirections()
    {
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;

        // Client to server
        var clientGuid = Guid.NewGuid();
        clientArea.NullableGuidValue = clientGuid;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.NullableGuidValue == clientGuid,
            timeout: TimeSpan.FromSeconds(90),
            message: () => $"Server should receive client's nullable Guid update. Expected: {clientGuid}, Actual: {serverArea.NullableGuidValue}");

        // Server to client
        var serverGuid = Guid.NewGuid();
        serverArea.NullableGuidValue = serverGuid;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.NullableGuidValue == serverGuid,
            timeout: TimeSpan.FromSeconds(90),
            message: () => $"Client should receive server's nullable Guid update. Expected: {serverGuid}, Actual: {clientArea.NullableGuidValue}");

        // Client sets null (OPC UA maps null to Guid.Empty since Guid is a value type)
        clientArea.NullableGuidValue = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.NullableGuidValue == Guid.Empty,
            timeout: TimeSpan.FromSeconds(90),
            message: () => $"Server should receive client's null→Empty Guid update. Actual: {serverArea.NullableGuidValue}");

        Logger.Log("Nullable Guid bidirectional sync verified");
    }

    [Fact]
    public async Task WhenEnumChanges_ThenItSyncsBothDirections()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;

        // Act
        serverArea.EnumValue = DayOfWeek.Wednesday;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.EnumValue == DayOfWeek.Wednesday,
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the server's enum update");

        // Act
        clientArea.EnumValue = DayOfWeek.Friday;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.EnumValue == DayOfWeek.Friday,
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive the client's enum update");
    }

    [Fact]
    public async Task WhenEnumArrayChanges_ThenItSyncsBothDirections()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;
        DayOfWeek[] serverValues = [DayOfWeek.Monday, DayOfWeek.Sunday];
        DayOfWeek[] clientValues = [DayOfWeek.Saturday];

        // Act
        serverArea.EnumArray = serverValues;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.EnumArray.SequenceEqual(serverValues),
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the server's enum array update");

        // Act
        clientArea.EnumArray = clientValues;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.EnumArray.SequenceEqual(clientValues),
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive the client's enum array update");
    }

    [Fact]
    public async Task WhenSByteArrayChanges_ThenItSyncsBothDirections()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;
        sbyte[] serverValues = [-1, 0, 127];
        sbyte[] clientValues = [-128, 5];

        // Act
        serverArea.SByteArray = serverValues;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.SByteArray.SequenceEqual(serverValues),
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the server's sbyte[] update");

        // Act
        clientArea.SByteArray = clientValues;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => serverArea.SByteArray.SequenceEqual(clientValues),
            timeout: TimeSpan.FromSeconds(90),
            message: "Server should receive the client's sbyte[] update");
    }

    [Fact]
    public async Task WhenDateTimeIsMinOrMaxValue_ThenClientReceivesItUnchanged()
    {
        // Arrange
        var serverArea = ServerFixture.ServerRoot.DataTypes;
        var clientArea = Client!.Root!.DataTypes;
        var date = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        serverArea.DefaultDateTimeValue = date;
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.DefaultDateTimeValue == date,
            timeout: TimeSpan.FromSeconds(90),
            message: "Client should receive the server's DateTime update");

        // Act
        serverArea.DefaultDateTimeValue = DateTime.MinValue;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.DefaultDateTimeValue == DateTime.MinValue,
            timeout: TimeSpan.FromSeconds(90),
            message: () => $"Client should receive DateTime.MinValue, but has {clientArea.DefaultDateTimeValue:O}");

        // Act
        serverArea.DefaultDateTimeValue = DateTime.MaxValue;

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(
            () => clientArea.DefaultDateTimeValue.Ticks == DateTime.MaxValue.Ticks,
            timeout: TimeSpan.FromSeconds(90),
            message: () => $"Client should receive DateTime.MaxValue, but has {clientArea.DefaultDateTimeValue:O}");
    }
}
