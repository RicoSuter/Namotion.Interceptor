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
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        var payload = MatterPayload.Create(writer =>
        {
            writer.WriteStartStruct();
            writer.WriteFieldName(0, "level");
            writer.WriteUInt64(128);
            writer.WriteEndStruct();
        });

        // Act
        var response = await session.Controller.InvokeAsync(0xFFFFFFFE00000001, new CommandPath(1, 8, 4), "moveToLevelWithOnOff", payload, TimeSpan.FromSeconds(2));

        // Assert
        var request = await session.Server.ReadRequestAsync("device_command");
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
        await using var session = await FakeMatterServer.StartAndConnectAsync();

        // Act
        await session.Controller.InvokeAsync(1, new CommandPath(1, 6, 2), "toggle");

        // Assert
        var request = await session.Server.ReadRequestAsync("device_command");
        Assert.Equal("{}", request["args"]!["payload"]!.ToJsonString());
        Assert.Null(request["args"]!["timed_request_timeout_ms"]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task WhenTimedRequestTimeoutIsOutOfRange_ThenArgumentOutOfRangeExceptionIsThrown(int milliseconds)
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.Controller.InvokeAsync(1, new CommandPath(1, 257, 0), "lockDoor", timedRequestTimeout: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task WhenServerAnswersWithError_ThenMatterExceptionCarriesStatusAndReason()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.Handler = _ => throw new FakeMatterServerErrorException(new JsonObject
        {
            ["error_code"] = 1,
            ["details"] = "Wrong code",
            ["status"] = 2,
            ["cluster_status"] = 3,
            ["reason"] = "passcode-mismatch"
        });

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MatterException>(() => session.Controller.CommissionAsync("34970112332", networkOnly: true));
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
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.Handler = _ => new JsonArray(new JsonObject
        {
            ["Path"] = new JsonObject { ["EndpointId"] = 1, ["ClusterId"] = 6, ["AttributeId"] = 16385 },
            ["Status"] = 135
        });
        var value = MatterPayload.Create(writer => writer.WriteUInt64(10));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MatterException>(() => session.Controller.WriteAttributeAsync(1, new AttributePath(1, 6, 16385), value));
        Assert.Equal(135, exception.Status);
        var request = await session.Server.ReadRequestAsync("write_attribute");
        Assert.Equal("1/6/16385", request["args"]!["attribute_path"]!.GetValue<string>());
        Assert.Equal(10, request["args"]!["value"]!.GetValue<int>());
    }

    [Fact]
    public async Task WhenWriteIsAnsweredWithoutStatusList_ThenItSucceeds()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();

        // Act: the fake answers with null
        var exception = await Record.ExceptionAsync(() =>
            session.Controller.WriteAttributeAsync(1, new AttributePath(0, 6, 16385), MatterPayload.Create(writer => writer.WriteUInt64(10))));

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task WhenServerAnswersWithMalformedError_ThenOnlyThatRequestFails()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.Handler = request => request["command"]!.GetValue<string>() == "remove_node"
            ? throw new FakeMatterServerErrorException(new JsonObject { ["error_code"] = "not a number" })
            : new JsonObject { ["type"] = "manual", ["setup_pin_code"] = 20202021 };

        // Act
        var exception = await Assert.ThrowsAsync<MatterException>(() => session.Controller.RemoveNodeAsync(1));
        var payloads = await session.Controller.ParseOnboardingPayloadAsync("34970112332");

        // Assert
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(20202021u, Assert.Single(payloads).SetupPinCode);
    }

    [Fact]
    public async Task WhenParsingManualCode_ThenPayloadIsRead()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.Handler = _ => new JsonObject { ["type"] = "manual", ["short_discriminator"] = 15, ["setup_pin_code"] = 20202021 };

        // Act
        var payloads = await session.Controller.ParseOnboardingPayloadAsync("34970112332");

        // Assert
        var payload = Assert.Single(payloads);
        Assert.Equal(new MatterOnboardingPayload(20202021, null, 15, null, null, null), payload);
    }

    [Fact]
    public async Task WhenParsingQrCode_ThenEveryPayloadIsRead()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.Handler = _ => new JsonObject
        {
            ["type"] = "qr",
            ["payloads"] = new JsonArray(new JsonObject
            {
                ["version"] = 0, ["vendor_id"] = 65521, ["product_id"] = 32768, ["flow_type"] = 0,
                ["discovery_capabilities"] = 4, ["discriminator"] = 3840, ["setup_pin_code"] = 20202021
            })
        };

        // Act
        var payloads = await session.Controller.ParseOnboardingPayloadAsync("MT:Y.K9042C00KA0648G00");

        // Assert
        var payload = Assert.Single(payloads);
        Assert.Equal(new MatterOnboardingPayload(20202021, 3840, null, 65521, 32768, MatterDiscoveryCapabilities.OnNetwork), payload);
    }

    [Fact]
    public async Task WhenSettingCredentials_ThenArgumentsAreSent()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();

        // Act
        await session.Controller.SetWifiCredentialsAsync("home", "secret");
        await session.Controller.SetThreadDatasetAsync("0e080000000000010000");

        // Assert
        var wifi = await session.Server.ReadRequestAsync("set_wifi_credentials");
        Assert.Equal("home", wifi["args"]!["ssid"]!.GetValue<string>());
        Assert.Equal("secret", wifi["args"]!["credentials"]!.GetValue<string>());
        var thread = await session.Server.ReadRequestAsync("set_thread_dataset");
        Assert.Equal("0e080000000000010000", thread["args"]!["dataset"]!.GetValue<string>());
    }

    [Fact]
    public async Task WhenOpeningCommissioningWindow_ThenCodesAreRead()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.Handler = _ => new JsonObject
        {
            ["setup_pin_code"] = 12345678, ["setup_manual_code"] = "35325335079", ["setup_qr_code"] = "MT:Y.ABCDEFG123456789"
        };

        // Act
        var window = await session.Controller.OpenCommissioningWindowAsync(5, TimeSpan.FromMinutes(5));

        // Assert
        Assert.Equal(new MatterCommissioningWindow(12345678, "35325335079", "MT:Y.ABCDEFG123456789"), window);
        var request = await session.Server.ReadRequestAsync("open_commissioning_window");
        Assert.Equal(300, request["args"]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public async Task WhenServerDoesNotAnswer_ThenRequestTimesOut()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync(new MatterServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        session.Server.LeaveUnansweredRequestsOpen = true;

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() => session.Controller.RemoveNodeAsync(1));
    }

    [Fact]
    public async Task WhenConcurrentRequestsAreAnsweredOutOfOrder_ThenEachGetsItsOwnResult()
    {
        // Arrange
        const int requestCount = 20;
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.LeaveUnansweredRequestsOpen = true;
        var codes = Enumerable.Range(1, requestCount).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray();

        // Act
        var resultsTask = Task.WhenAll(codes.Select(code => session.Controller.ParseOnboardingPayloadAsync(code)));
        var requests = new List<JsonObject>();
        while (requests.Count < requestCount)
        {
            requests.Add(await session.Server.Requests.Reader.ReadWithTimeoutAsync());
        }

        for (var index = requests.Count - 1; index >= 0; index--)
        {
            var code = requests[index]["args"]!["code"]!.GetValue<string>();
            await session.Server.AnswerAsync(requests[index], new JsonObject { ["type"] = "manual", ["setup_pin_code"] = uint.Parse(code, CultureInfo.InvariantCulture) });
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
        await using var session = await FakeMatterServer.StartAndConnectAsync();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.Controller.OpenCommissioningWindowAsync(5, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task WhenNodeIdIsGroupId_ThenArgumentOutOfRangeExceptionIsThrown()
    {
        // Arrange
        const ulong groupNodeId = 0xFFFFFFFFFFFF0001;
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        var value = MatterPayload.Create(writer => writer.WriteUInt64(10));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.Controller.WriteAttributeAsync(groupNodeId, new AttributePath(1, 6, 16385), value));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.Controller.InvokeAsync(groupNodeId, new CommandPath(1, 6, 2), "toggle"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.Controller.RemoveNodeAsync(groupNodeId));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.Controller.OpenCommissioningWindowAsync(groupNodeId, TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData("start_listening")]
    [InlineData("parse_onboarding_payload")]
    [InlineData("open_commissioning_window")]
    [InlineData("write_attribute")]
    public async Task WhenResultIsMalformed_ThenMatterExceptionWrapsTheReadError(string command)
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.Handler = _ => command == "write_attribute" ? new JsonArray(new JsonObject { ["Status"] = "busy" }) : new JsonObject { ["unexpected"] = true };

        // Act
        var exception = await Assert.ThrowsAsync<MatterException>(() => command switch
        {
            "start_listening" => session.Controller.StartListeningAsync(),
            "parse_onboarding_payload" => session.Controller.ParseOnboardingPayloadAsync("34970112332"),
            "open_commissioning_window" => session.Controller.OpenCommissioningWindowAsync(5, TimeSpan.FromMinutes(5)),
            _ => session.Controller.WriteAttributeAsync(1, new AttributePath(1, 6, 16385), MatterPayload.Create(writer => writer.WriteUInt64(10)))
        });

        // Assert
        Assert.Contains(command, exception.Message);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public async Task WhenConnectionIsLostWhileRequestIsPending_ThenRequestFailsWithWebSocketException()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.LeaveUnansweredRequestsOpen = true;
        var request = session.Controller.RemoveNodeAsync(1);
        await session.Server.ReadRequestAsync("remove_node");

        // Act
        session.Server.CloseClient();

        // Assert
        var exception = await Assert.ThrowsAsync<WebSocketException>(() => request);
        var closeError = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.Events.WaitForCompletionAsync());
        Assert.Same(closeError, exception.InnerException);
    }

    [Fact]
    public async Task WhenControllerIsDisposedWhileRequestIsPending_ThenRequestThrowsObjectDisposedException()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.LeaveUnansweredRequestsOpen = true;
        var request = session.Controller.RemoveNodeAsync(1);
        await session.Server.ReadRequestAsync("remove_node");

        // Act
        await session.Controller.DisposeAsync();

        // Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => request);
        await session.Controller.Events.WaitForCompletionAsync();
    }

    [Fact]
    public async Task WhenCallerCancelsRequest_ThenOperationCanceledExceptionIsThrownAndConnectionStaysUsable()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        session.Server.LeaveUnansweredRequestsOpen = true;
        using var cancellation = new CancellationTokenSource();
        var request = session.Controller.RemoveNodeAsync(1, cancellation.Token);
        var openRequest = await session.Server.ReadRequestAsync("remove_node");

        // Act
        await cancellation.CancelAsync();

        // Assert
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        await session.Server.AnswerAsync(openRequest, null);
        var payloads = session.Controller.ParseOnboardingPayloadAsync("34970112332");
        await session.Server.AnswerAsync(await session.Server.ReadRequestAsync("parse_onboarding_payload"), new JsonObject { ["type"] = "manual", ["setup_pin_code"] = 20202021 });
        Assert.Equal(20202021u, Assert.Single(await payloads).SetupPinCode);
    }

    [Fact]
    public async Task WhenCommissioningIsCanceled_ThenOperationCanceledExceptionIsThrown()
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync(new MatterServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(200) });
        session.Server.LeaveUnansweredRequestsOpen = true;
        using var cancellation = new CancellationTokenSource();
        var commissioning = session.Controller.CommissionAsync("34970112332", networkOnly: true, cancellation.Token);
        await session.Server.ReadRequestAsync("commission_with_code");

        // A request sent later times out, while commissioning, which has no request timeout, keeps waiting
        await Assert.ThrowsAsync<TimeoutException>(() => session.Controller.RemoveNodeAsync(1));
        Assert.False(commissioning.IsCompleted);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commissioning.WaitAsync(TestTimeouts.Default));
    }

    [Fact]
    public async Task WhenSendTimesOut_ThenRequestTimesOutAndConnectionFails()
    {
        // Arrange: a Unix socket has a small fixed send buffer, so a large request blocks while the server does not read
        await using var session = await FakeMatterServer.StartAndConnectAsync(new MatterServerOptions { RequestTimeout = TimeSpan.FromMilliseconds(500) }, useUnixSocket: true);
        session.Server.PauseReceiving = true;
        var largeValue = MatterPayload.Create(writer => writer.WriteBytes(new byte[12 * 1024 * 1024]));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<TimeoutException>(() => session.Controller.WriteAttributeAsync(1, new AttributePath(1, 6, 16385), largeValue));
        Assert.Contains("accept", exception.Message);
        await Assert.ThrowsAsync<TimeoutException>(() => session.Controller.Events.WaitForCompletionAsync());
        var later = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.RemoveNodeAsync(1));
        Assert.IsType<TimeoutException>(later.InnerException);
    }

    [Fact]
    public async Task WhenServerStopsAnsweringPings_ThenKeepAliveFailsTheConnection()
    {
        // Arrange: a WebSocket server answers pings only while it reads, so one that stops reading misses them
        var options = new MatterServerOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(100), KeepAliveTimeout = TimeSpan.FromMilliseconds(200) };
        await using var session = await FakeMatterServer.StartAndConnectAsync(options);
        session.Server.LeaveUnansweredRequestsOpen = true;
        session.Server.PauseReceiving = true;

        // Act: the request is the last message the server reads
        var request = session.Controller.RemoveNodeAsync(1);
        await session.Server.ReadRequestAsync("remove_node");

        // Assert
        var closeError = await Assert.ThrowsAsync<WebSocketException>(() => session.Controller.Events.WaitForCompletionAsync());
        var exception = await Assert.ThrowsAsync<WebSocketException>(() => request);
        Assert.Same(closeError, exception.InnerException);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WhenPayloadDoesNotCloseWhatItOpens_ThenArgumentExceptionIsThrownBeforeSending(bool leavesStructOpen)
    {
        // Arrange
        await using var session = await FakeMatterServer.StartAndConnectAsync();
        var payload = MatterPayload.Create(writer =>
        {
            if (leavesStructOpen)
            {
                writer.WriteStartStruct();
                writer.WriteFieldName(0, "level");
                writer.WriteUInt64(1);
            }
            else
            {
                writer.WriteUInt64(1);
                writer.WriteEndStruct();
            }
        });

        // Act
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => session.Controller.InvokeAsync(1, new CommandPath(1, 8, 0), "moveToLevel", payload));

        // Assert: the next command is the first one the server receives
        Assert.Equal("payload", exception.ParamName);
        await session.Controller.InvokeAsync(1, new CommandPath(1, 6, 2), "toggle");
        var request = await session.Server.ReadRequestAsync("device_command");
        Assert.Equal("toggle", request["args"]!["command_name"]!.GetValue<string>());
    }
}
