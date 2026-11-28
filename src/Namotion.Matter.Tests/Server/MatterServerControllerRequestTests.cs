using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Namotion.Interceptor.Testing;
using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Server;

public class MatterServerControllerRequestTests
{
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
        var request = await server.ReadRequestAsync("device_command");
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
        var request = await server.ReadRequestAsync("device_command");
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
        var request = await server.ReadRequestAsync("write_attribute");
        Assert.Equal("1/6/16385", request["args"]!["attribute_path"]!.GetValue<string>());
        Assert.Equal(10, request["args"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task WhenWriteIsAnsweredWithoutStatusList_ThenItSucceeds()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act: the fake answers with null
        var exception = await Record.ExceptionAsync(() =>
            controller.WriteAttributeAsync(1, new AttributePath(0, 6, 16385), MatterPayload.Create(writer => writer.WriteUInt64(10))));

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
        var wifi = await server.ReadRequestAsync("set_wifi_credentials");
        Assert.Equal("home", wifi["args"]!["ssid"]!.GetValue<string>());
        Assert.Equal("secret", wifi["args"]!["credentials"]!.GetValue<string>());
        var thread = await server.ReadRequestAsync("set_thread_dataset");
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
        var request = await server.ReadRequestAsync("open_commissioning_window");
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
            requests.Add(await server.Requests.Reader.ReadWithTimeoutAsync());
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

    [Theory]
    [InlineData(179)]
    [InlineData(901)]
    [InlineData(300.5)]
    public async Task WhenCommissioningWindowTimeoutIsInvalid_ThenArgumentOutOfRangeExceptionIsThrown(double seconds)
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.OpenCommissioningWindowAsync(5, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task WhenNodeIdIsGroupId_ThenArgumentOutOfRangeExceptionIsThrown()
    {
        // Arrange
        const ulong groupNodeId = 0xFFFFFFFFFFFF0001;
        await using var server = await FakeMatterServer.StartAsync();
        await using var controller = await MatterServerController.ConnectAsync(server.Url);
        var value = MatterPayload.Create(writer => writer.WriteUInt64(10));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.WriteAttributeAsync(groupNodeId, new AttributePath(1, 6, 16385), value));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.InvokeAsync(groupNodeId, new CommandPath(1, 6, 2), "toggle"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.RemoveNodeAsync(groupNodeId));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.OpenCommissioningWindowAsync(groupNodeId, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData("start_listening")]
    [InlineData("parse_onboarding_payload")]
    [InlineData("open_commissioning_window")]
    [InlineData("write_attribute")]
    public async Task WhenResultIsMalformed_ThenMatterExceptionWrapsTheReadError(string command)
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.Handler = _ => command == "write_attribute" ? new JsonArray(new JsonObject { ["Status"] = "busy" }) : new JsonObject { ["unexpected"] = true };
        await using var controller = await MatterServerController.ConnectAsync(server.Url);

        // Act
        var exception = await Assert.ThrowsAsync<MatterException>(() => command switch
        {
            "start_listening" => controller.StartListeningAsync(),
            "parse_onboarding_payload" => controller.ParseOnboardingPayloadAsync("34970112332"),
            "open_commissioning_window" => controller.OpenCommissioningWindowAsync(5, TimeSpan.FromMinutes(5)),
            _ => controller.WriteAttributeAsync(1, new AttributePath(1, 6, 16385), MatterPayload.Create(writer => writer.WriteUInt64(10)))
        });

        // Assert
        Assert.Contains(command, exception.Message);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task WhenConnectionIsLostWhileRequestIsPending_ThenRequestFailsWithWebSocketException()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.LeaveUnansweredRequestsOpen = true;
        await using var controller = await MatterServerController.ConnectAsync(server.Url);
        var request = controller.RemoveNodeAsync(1);
        await server.ReadRequestAsync("remove_node");

        // Act
        server.CloseClient();

        // Assert
        var exception = await Assert.ThrowsAsync<WebSocketException>(() => request);
        var closeError = await Assert.ThrowsAsync<WebSocketException>(() => controller.Events.WaitForCompletionAsync());
        Assert.Same(closeError, exception.InnerException);
    }

    [Fact]
    public async Task WhenControllerIsDisposedWhileRequestIsPending_ThenRequestThrowsObjectDisposedException()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.LeaveUnansweredRequestsOpen = true;
        var controller = await MatterServerController.ConnectAsync(server.Url);
        var request = controller.RemoveNodeAsync(1);
        await server.ReadRequestAsync("remove_node");

        // Act
        await controller.DisposeAsync();

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
        await controller.Events.WaitForCompletionAsync();
    }

    [Fact]
    public async Task WhenCallerCancelsRequest_ThenOperationCanceledExceptionIsThrownAndConnectionStaysUsable()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.LeaveUnansweredRequestsOpen = true;
        await using var controller = await MatterServerController.ConnectAsync(server.Url);
        using var cancellation = new CancellationTokenSource();
        var request = controller.RemoveNodeAsync(1, cancellation.Token);
        var openRequest = await server.ReadRequestAsync("remove_node");

        // Act
        await cancellation.CancelAsync();

        // Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await server.AnswerAsync(openRequest, null);
        var payloads = controller.ParseOnboardingPayloadAsync("34970112332");
        await server.AnswerAsync(await server.ReadRequestAsync("parse_onboarding_payload"), new JsonObject { ["type"] = "manual", ["setup_pin_code"] = 20202021 });
        Assert.Equal(20202021u, Assert.Single(await payloads).SetupPinCode);
    }

    [Fact]
    public async Task WhenCommissioningIsCanceled_ThenOperationCanceledExceptionIsThrown()
    {
        // Arrange
        await using var server = await FakeMatterServer.StartAsync();
        server.LeaveUnansweredRequestsOpen = true;
        await using var controller = await MatterServerController.ConnectAsync(server.Url, new MatterServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        using var cancellation = new CancellationTokenSource();
        var commissioning = controller.CommissionAsync("34970112332", networkOnly: true, cancellation.Token);
        await server.ReadRequestAsync("commission_with_code");

        // Act
        await cancellation.CancelAsync();

        // Assert: the request timeout does not apply to commissioning, so only the cancellation ends it
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commissioning.WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenSendTimesOut_ThenRequestTimesOutAndConnectionFails()
    {
        // Arrange: a Unix socket has a small fixed send buffer, so a large request blocks while the server does not read
        var socketPath = Path.Combine(Path.GetTempPath(), $"matter-{Guid.NewGuid():N}.sock");
        await using var server = await FakeMatterServer.StartAsync(socketPath);
        await using var controller = await MatterServerController.ConnectAsync(server.Url, new MatterServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(500) });
        server.PauseReceiving = true;
        var largeValue = MatterPayload.Create(writer => writer.WriteBytes(new byte[12 * 1024 * 1024]));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => controller.WriteAttributeAsync(1, new AttributePath(1, 6, 16385), largeValue));
        Assert.Contains("accept", exception.Message);
        await Assert.ThrowsAsync<TimeoutException>(() => controller.Events.WaitForCompletionAsync());
        var later = await Assert.ThrowsAsync<WebSocketException>(() => controller.RemoveNodeAsync(1));
        Assert.IsType<TimeoutException>(later.InnerException);
    }

    // Keep-alive (MatterServerOptions.KeepAliveTimeout) is not tested: Kestrel answers pings itself, so a fake server
    // that ignores them would need a raw socket, and the test would have to wait for the keep-alive timeout.
}
