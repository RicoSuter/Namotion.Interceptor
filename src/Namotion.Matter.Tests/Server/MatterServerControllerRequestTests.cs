using System.Globalization;
using System.Text.Json.Nodes;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Server;

public class MatterServerControllerRequestTests
{
    private static async Task<JsonObject> ReadRequestAsync(FakeMatterServer server, string command)
    {
        await foreach (var request in server.Requests.Reader.ReadAllAsync())
        {
            if (request["command"]!.GetValue<string>() == command)
            {
                return request;
            }
        }

        throw new InvalidOperationException($"No '{command}' request was received.");
    }

    [Fact]
    public async Task WhenInvokingCommand_ThenRequestCarriesPathNameAndPayload()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);
        var payload = MatterPayload.Create(writer =>
        {
            writer.WriteStartStruct();
            writer.WriteFieldName(0, "level");
            writer.WriteUInt64(128);
            writer.WriteEndStruct();
        });

        // Act
        var response = await controller.InvokeAsync(0xFFFFFFFE00000001, new CommandPath(1, 8, 4), "moveToLevelWithOnOff", payload, TimeSpan.FromSeconds(2));

        // Assert
        var request = await ReadRequestAsync(server, "device_command");
        var arguments = request["args"]!;
        Assert.Equal(0xFFFFFFFE00000001, arguments["node_id"]!.GetValue<ulong>());
        Assert.Equal(1, arguments["endpoint_id"]!.GetValue<int>());
        Assert.Equal(8, arguments["cluster_id"]!.GetValue<int>());
        Assert.Equal("moveToLevelWithOnOff", arguments["command_name"]!.GetValue<string>());
        Assert.Equal(128, arguments["payload"]!["level"]!.GetValue<int>());
        Assert.Equal(2000, arguments["timed_request_timeout_ms"]!.GetValue<int>());
        Assert.True(response.IsNull);
    }

    [Fact]
    public async Task WhenInvokingWithoutPayload_ThenEmptyPayloadIsSent()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        await controller.InvokeAsync(1, new CommandPath(1, 6, 2), "toggle");

        // Assert
        var request = await ReadRequestAsync(server, "device_command");
        Assert.Equal("{}", request["args"]!["payload"]!.ToJsonString());
        Assert.Null(request["args"]!["timed_request_timeout_ms"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task WhenTimedRequestTimeoutIsOutOfRange_ThenArgumentOutOfRangeExceptionIsThrown(int milliseconds)
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            controller.InvokeAsync(1, new CommandPath(1, 257, 0), "lockDoor", timedRequestTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task WhenServerAnswersWithError_ThenMatterExceptionCarriesStatusAndReason()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.Handler = _ => throw new FakeMatterServerErrorException(new JsonObject
        {
            ["error_code"] = 1,
            ["details"] = "Wrong code",
            ["status"] = 2,
            ["cluster_status"] = 3,
            ["reason"] = "passcode-mismatch"
        });
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MatterException>(() => controller.CommissionAsync("34970112332", networkOnly: true));
        Assert.Equal(MatterErrorCode.NodeCommissionFailed, exception.ErrorCode);
        Assert.Equal("Wrong code", exception.Message);
        Assert.Equal(2, exception.Status);
        Assert.Equal(3, exception.ClusterStatus);
        Assert.Equal(MatterCommissioningFailureReason.PasscodeMismatch, exception.CommissioningFailureReason);
    }

    [Fact]
    public async Task WhenDeviceRejectsWrite_ThenMatterExceptionCarriesStatus()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.Handler = _ => new JsonArray(new JsonObject
        {
            ["Path"] = new JsonObject { ["EndpointId"] = 1, ["ClusterId"] = 6, ["AttributeId"] = 16385 },
            ["Status"] = 135
        });
        await using var controller = await MatterServerController.ConnectAsync(server.Url);
        var value = MatterPayload.Create(writer => writer.WriteUInt64(10));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MatterException>(() => controller.WriteAttributeAsync(1, new AttributePath(1, 6, 16385), value));
        Assert.Equal(135, exception.Status);
        var request = await ReadRequestAsync(server, "write_attribute");
        Assert.Equal("1/6/16385", request["args"]!["attribute_path"]!.GetValue<string>());
        Assert.Equal(10, request["args"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task WhenWriteIsAnsweredWithoutStatusList_ThenItSucceeds()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act: the fake answers with null, as the server answers a group write
        var exception = await Record.ExceptionAsync(() =>
            controller.WriteAttributeAsync(0xFFFFFFFFFFFF0001, new AttributePath(0, 6, 16385), MatterPayload.Create(writer => writer.WriteUInt64(10))));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenServerAnswersWithMalformedError_ThenOnlyThatRequestFails()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.Handler = request => request["command"]!.GetValue<string>() == "remove_node"
            ? throw new FakeMatterServerErrorException(new JsonObject { ["error_code"] = "not a number" })
            : new JsonObject { ["type"] = "manual", ["setup_pin_code"] = 20202021 };
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        var exception = await Assert.ThrowsAsync<MatterException>(() => controller.RemoveNodeAsync(1));
        var payloads = await controller.ParseOnboardingPayloadAsync("34970112332");

        // Assert
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(20202021u, Assert.Single(payloads).SetupPinCode);
    }

    [Fact]
    public async Task WhenParsingManualCode_ThenPayloadIsRead()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.Handler = _ => new JsonObject { ["type"] = "manual", ["short_discriminator"] = 15, ["setup_pin_code"] = 20202021 };
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        var payloads = await controller.ParseOnboardingPayloadAsync("34970112332");

        // Assert
        var payload = Assert.Single(payloads);
        Assert.Equal(new MatterOnboardingPayload(20202021, null, 15, null, null, null), payload);
    }

    [Fact]
    public async Task WhenParsingQrCode_ThenEveryPayloadIsRead()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.Handler = _ => new JsonObject
        {
            ["type"] = "qr",
            ["payloads"] = new JsonArray(new JsonObject
            {
                ["version"] = 0, ["vendor_id"] = 65521, ["product_id"] = 32768, ["flow_type"] = 0,
                ["discovery_capabilities"] = 4, ["discriminator"] = 3840, ["setup_pin_code"] = 20202021
            })
        };
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        var payloads = await controller.ParseOnboardingPayloadAsync("MT:Y.K9042C00KA0648G00");

        // Assert
        var payload = Assert.Single(payloads);
        Assert.Equal(new MatterOnboardingPayload(20202021, 3840, null, 65521, 32768, MatterDiscoveryCapabilities.OnNetwork), payload);
    }

    [Fact]
    public async Task WhenSettingCredentials_ThenArgumentsAreSent()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        await controller.SetWifiCredentialsAsync("home", "secret");
        await controller.SetThreadDatasetAsync("0e080000000000010000");

        // Assert
        var wifi = await ReadRequestAsync(server, "set_wifi_credentials");
        Assert.Equal("home", wifi["args"]!["ssid"]!.GetValue<string>());
        Assert.Equal("secret", wifi["args"]!["credentials"]!.GetValue<string>());
        var thread = await ReadRequestAsync(server, "set_thread_dataset");
        Assert.Equal("0e080000000000010000", thread["args"]!["dataset"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhenOpeningCommissioningWindow_ThenCodesAreRead()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.Handler = _ => new JsonObject
        {
            ["setup_pin_code"] = 12345678, ["setup_manual_code"] = "35325335079", ["setup_qr_code"] = "MT:Y.ABCDEFG123456789"
        };
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        var window = await controller.OpenCommissioningWindowAsync(5, TimeSpan.FromMinutes(5));

        // Assert
        Assert.Equal(new MatterCommissioningWindow(12345678, "35325335079", "MT:Y.ABCDEFG123456789"), window);
        var request = await ReadRequestAsync(server, "open_commissioning_window");
        Assert.Equal(300, request["args"]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public async Task WhenServerDoesNotAnswer_ThenRequestTimesOut()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.LeaveUnansweredRequestsOpen = true;
        await using var controller = await MatterServerController.ConnectAsync(server.Url, new MatterServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) });

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() => controller.RemoveNodeAsync(1));
    }

    [Fact]
    public async Task WhenConcurrentRequestsAreAnsweredOutOfOrder_ThenEachGetsItsOwnResult()
    {
        // Arrange
        const int requestCount = 20;
        await using var server = await FakeMatterServer.StartAsync();
        server.LeaveUnansweredRequestsOpen = true;
        await using var controller = await MatterServerController.ConnectAsync(server.Url);
        var codes = Enumerable.Range(1, requestCount).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray();

        // Act
        var resultsTask = Task.WhenAll(codes.Select(code => controller.ParseOnboardingPayloadAsync(code)));
        var requests = new List<JsonObject>();
        while (requests.Count < requestCount)
        {
            requests.Add(await server.Requests.Reader.ReadAsync());
        }

        for (var index = requests.Count - 1; index >= 0; index--)
        {
            var code = requests[index]["args"]!["code"]!.GetValue<string>();
            await server.AnswerAsync(requests[index], new JsonObject { ["type"] = "manual", ["setup_pin_code"] = uint.Parse(code, CultureInfo.InvariantCulture) });
        }

        var results = await resultsTask;

        // Assert
        Assert.Equal(Enumerable.Range(1, requestCount).Select(index => (uint)index), results.Select(payloads => payloads[0].SetupPinCode));
    }
}
