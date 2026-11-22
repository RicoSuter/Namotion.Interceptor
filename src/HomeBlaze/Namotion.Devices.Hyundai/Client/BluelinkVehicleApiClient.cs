using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HomeBlaze.Abstractions.Common;
using Namotion.Devices.Hyundai.Mapping;

namespace Namotion.Devices.Hyundai.Client;

/// <summary>The access token, device and vehicle of a vehicle API call.</summary>
internal readonly record struct BluelinkVehicleRequest(string AccessToken, string DeviceId, string VehicleId, BluelinkProtocol Protocol)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("VehicleId = ").Append(VehicleId).Append(", Protocol = ").Append(Protocol);
        return true;
    }
}

/// <summary>A vehicle of the vehicle list; a blank nickname or model name is null.</summary>
internal sealed record BluelinkVehicleInfo(string VehicleId, string Vin, string? Nickname, string? ModelName, string? Type, BluelinkProtocol Protocol)
{
    /// <summary>Whether the vehicle is a battery electric vehicle, the only type the integration supports.</summary>
    public bool IsElectric => Type == "EV";
}

/// <summary>
/// The calls to the vehicle API (<c>prd.eu-ccapi.hyundai.com:8080</c>). Every call is counted by the budget before it is
/// sent and throws <see cref="BluelinkRequestLimitException"/> without sending once the budget is spent.
/// </summary>
internal sealed class BluelinkVehicleApiClient(HttpClient httpClient, TimeProvider timeProvider, BluelinkRequestBudget budget)
{
    private const string SpaV1 = BluelinkAppConstants.VehicleApiHost + "/api/v1/spa/";
    private const string SpaV2 = BluelinkAppConstants.VehicleApiHost + "/api/v2/spa/";
    private const string UserApi = BluelinkAppConstants.VehicleApiHost + "/api/v1/user/";

    /// <summary>Registers this installation as a device of the account; the returned ID is kept for all later calls.</summary>
    public async Task<string> RegisterDeviceAsync(CancellationToken cancellationToken)
    {
        var body = BluelinkJson.Write(writer =>
        {
            writer.WriteString("pushRegId", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
            writer.WriteString("pushType", BluelinkAppConstants.PushType);
            writer.WriteString("uuid", Guid.NewGuid().ToString());
        });

        var root = await SendAsync(HttpMethod.Post, SpaV1 + "notifications/register", default(CallIdentity), body, null, cancellationToken);
        return root.GetString("resMsg.deviceId") is { Length: > 0 } deviceId
            ? deviceId
            : throw new BluelinkApiException(HttpStatusCode.OK, null, "The device registration returned no device ID.");
    }

    public async Task<IReadOnlyList<BluelinkVehicleInfo>> GetVehiclesAsync(string accessToken, string deviceId, CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Get, SpaV1 + "vehicles", new CallIdentity(accessToken, deviceId, BluelinkProtocol.Legacy), null, null, cancellationToken);
        var vehicles = new List<BluelinkVehicleInfo>();
        if (root.Find("resMsg.vehicles") is { ValueKind: JsonValueKind.Array } entries)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.GetString("vehicleId") is not { Length: > 0 } vehicleId || entry.GetString("vin") is not { Length: > 0 } vin)
                {
                    continue;
                }

                vehicles.Add(new BluelinkVehicleInfo(
                    vehicleId, vin, GetText(entry, "nickname"), GetText(entry, "vehicleName"), entry.GetString("type"),
                    entry.GetInt32("ccuCCS2ProtocolSupport") is > 0 ? BluelinkProtocol.Ccs2 : BluelinkProtocol.Legacy));
            }
        }

        return vehicles;
    }

    /// <summary>Reads the status that the server stored, for both protocols. Never reaches the car.</summary>
    /// <returns>The <c>state.Vehicle</c> tree of the CCS2 status.</returns>
    public async Task<JsonElement> GetStoredStatusAsync(BluelinkVehicleRequest request, CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Get, $"{SpaV1}vehicles/{request.VehicleId}/ccs2/carstatus/latest", request, null, null, cancellationToken);
        return root.Find("resMsg.state.Vehicle") ?? throw new BluelinkApiException(HttpStatusCode.OK, null, "The status has no vehicle state.");
    }

    /// <summary>Reads the stored parking location. Never reaches the car.</summary>
    public async Task<GeoCoordinate?> GetParkLocationAsync(BluelinkVehicleRequest request, CancellationToken cancellationToken)
    {
        // The reference implementation sends its default headers here, so the protocol header is 0 for every vehicle.
        var identity = new CallIdentity(request.AccessToken, request.DeviceId, BluelinkProtocol.Legacy);
        var root = await SendAsync(HttpMethod.Get, $"{SpaV1}vehicles/{request.VehicleId}/location/park", identity, null, null, cancellationToken);
        var location = root.Find("resMsg.gpsDetail") ?? root.Find("resMsg");
        return location is { } element && element.GetDecimal("coord.lat") is { } latitude && element.GetDecimal("coord.lon") is { } longitude
            ? new GeoCoordinate((double)latitude, (double)longitude)
            : null;
    }

    /// <summary>Asks the car to upload its status. Reaches the car and costs 12 V battery.</summary>
    /// <exception cref="TimeoutException">The car did not answer within 90 seconds.</exception>
    public Task RequestStatusAsync(BluelinkVehicleRequest request, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, request.Protocol == BluelinkProtocol.Legacy
            ? $"{SpaV1}vehicles/{request.VehicleId}/status"
            : $"{SpaV1}vehicles/{request.VehicleId}/ccs2/carstatus", request, null, null, cancellationToken, waitsForCar: true);

    /// <returns>The message ID of the command.</returns>
    public Task<string> SetChargingAsync(BluelinkVehicleRequest request, bool start, string? controlToken, CancellationToken cancellationToken)
    {
        RequireControlToken(request, controlToken);
        var action = start ? "start" : "stop";
        if (request.Protocol == BluelinkProtocol.Legacy)
        {
            var body = BluelinkJson.Write(writer =>
            {
                writer.WriteString("action", action);
                writer.WriteString("deviceId", request.DeviceId);
            });
            return SendCommandAsync($"{SpaV1}vehicles/{request.VehicleId}/control/charge", request, body, null, cancellationToken);
        }

        return SendCommandAsync($"{SpaV2}vehicles/{request.VehicleId}/ccs2/control/charge", request, CreateCcs2Command(action), controlToken, cancellationToken);
    }

    /// <summary>Starts the climate for 1 to 30 minutes.</summary>
    /// <returns>The message ID of the command.</returns>
    public Task<string> StartClimateAsync(BluelinkVehicleRequest request, decimal temperature, bool defrost, int minutes, string? controlToken, CancellationToken cancellationToken)
    {
        RequireControlToken(request, controlToken);
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 30);
        if (request.Protocol == BluelinkProtocol.Legacy)
        {
            var temperatureCode = BluelinkValues.ToTemperatureCode(temperature);
            var body = BluelinkJson.Write(writer =>
            {
                writer.WriteString("action", "start");
                writer.WriteNumber("hvacType", 0);
                writer.WriteStartObject("options");
                writer.WriteBoolean("defrost", defrost);
                writer.WriteNumber("heating1", 0);
                writer.WriteNumber("igniOnDuration", minutes);
                writer.WriteEndObject();
                writer.WriteString("tempCode", temperatureCode);
                writer.WriteString("unit", "C");
            });
            return SendCommandAsync($"{SpaV1}vehicles/{request.VehicleId}/control/temperature", request, body, null, cancellationToken);
        }

        BluelinkValues.ValidateTemperature(temperature);
        var command = BluelinkJson.Write(writer =>
        {
            writer.WriteString("command", "start");
            writer.WriteNumber("ignitionDuration", minutes);
            writer.WriteNull("strgWhlHeating");
            writer.WriteNumber("hvacTempType", 1);
            writer.WriteNumber("hvacTemp", temperature);
            writer.WriteNumber("sideRearMirrorHeating", 0);
            // Left-hand drive, as all supported European vehicles.
            writer.WriteString("drvSeatLoc", "L");
            writer.WriteStartObject("seatClimateInfo");
            writer.WriteNull("drvSeatClimateState");
            writer.WriteNull("psgSeatClimateState");
            writer.WriteNull("rrSeatClimateState");
            writer.WriteNull("rlSeatClimateState");
            writer.WriteEndObject();
            writer.WriteString("tempUnit", "C");
            writer.WriteBoolean("windshieldFrontDefogState", defrost);
        });
        return SendCommandAsync($"{SpaV2}vehicles/{request.VehicleId}/ccs2/control/temperature", request, command, controlToken, cancellationToken);
    }

    /// <returns>The message ID of the command.</returns>
    public Task<string> StopClimateAsync(BluelinkVehicleRequest request, string? controlToken, CancellationToken cancellationToken)
    {
        RequireControlToken(request, controlToken);
        if (request.Protocol == BluelinkProtocol.Legacy)
        {
            // The payload of the official app for a stop; only the action is evaluated.
            var body = BluelinkJson.Write(writer =>
            {
                writer.WriteString("action", "stop");
                writer.WriteNumber("hvacType", 0);
                writer.WriteStartObject("options");
                writer.WriteBoolean("defrost", true);
                writer.WriteNumber("heating1", 1);
                writer.WriteEndObject();
                writer.WriteString("tempCode", "10H");
                writer.WriteString("unit", "C");
            });
            return SendCommandAsync($"{SpaV1}vehicles/{request.VehicleId}/control/temperature", request, body, null, cancellationToken);
        }

        return SendCommandAsync($"{SpaV2}vehicles/{request.VehicleId}/ccs2/control/temperature", request, CreateCcs2Command("stop"), controlToken, cancellationToken);
    }

    /// <summary>Sets both charge limits in percent; the API always takes both. Needs no PIN.</summary>
    /// <returns>The message ID of the command.</returns>
    public Task<string> SetChargeTargetsAsync(BluelinkVehicleRequest request, int acPercent, int dcPercent, CancellationToken cancellationToken)
    {
        var body = BluelinkJson.Write(writer =>
        {
            writer.WriteStartArray("targetSOClist");
            writer.WriteStartObject();
            writer.WriteNumber("plugType", 0);
            writer.WriteNumber("targetSOClevel", dcPercent);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteNumber("plugType", 1);
            writer.WriteNumber("targetSOClevel", acPercent);
            writer.WriteEndObject();
            writer.WriteEndArray();
        });
        return SendCommandAsync($"{SpaV1}vehicles/{request.VehicleId}/charge/target", request, body, null, cancellationToken);
    }

    /// <summary>Reads the result of a command from the notification records of the vehicle. Never reaches the car.</summary>
    public async Task<BluelinkCommandResult> GetCommandResultAsync(BluelinkVehicleRequest request, string messageId, CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Get, $"{SpaV1}notifications/{request.VehicleId}/records", request, null, null, cancellationToken);
        if (root.Find("resMsg") is { ValueKind: JsonValueKind.Array } records)
        {
            foreach (var record in records.EnumerateArray())
            {
                if (record.GetString("recordId") == messageId)
                {
                    return record.GetString("result") switch
                    {
                        "success" => BluelinkCommandResult.Success,
                        "fail" => BluelinkCommandResult.Failed,
                        "non-response" => BluelinkCommandResult.NoResponse,
                        _ => BluelinkCommandResult.Pending
                    };
                }
            }
        }

        return BluelinkCommandResult.Pending;
    }

    /// <summary>Exchanges the PIN for a control token, which CCS2 charging and climate commands need.</summary>
    /// <exception cref="BluelinkPinRejectedException">Hyundai rejected the PIN. Five wrong PINs lock PIN entry for a while.</exception>
    /// <exception cref="BluelinkApiException">The exchange failed for another reason.</exception>
    public async Task<BluelinkCcsToken> GetControlTokenAsync(string accessToken, string deviceId, string pin, CancellationToken cancellationToken)
    {
        budget.Consume();
        using var message = new HttpRequestMessage(HttpMethod.Put, UserApi + "pin?token=");
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        message.Headers.TryAddWithoutValidation("User-Agent", BluelinkAppConstants.VehicleApiUserAgent);
        message.Content = new StringContent(BluelinkJson.Write(writer =>
        {
            writer.WriteString("deviceId", deviceId);
            writer.WriteString("pin", pin);
        }), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendWithTimeoutAsync(timeProvider, message, waitsForCar: false, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return BluelinkVehicleApiResponses.ParseControlToken(response.StatusCode, text, timeProvider.GetUtcNow());
    }

    /// <summary>The optional access token, device and protocol of a call; the device registration sends none of them.</summary>
    private readonly record struct CallIdentity(string? AccessToken, string? DeviceId, BluelinkProtocol? Protocol)
    {
        private bool PrintMembers(StringBuilder builder)
        {
            builder.Append("DeviceId = ").Append(DeviceId).Append(", Protocol = ").Append(Protocol);
            return true;
        }
    }

    private static void RequireControlToken(BluelinkVehicleRequest request, string? controlToken)
    {
        if (request.Protocol == BluelinkProtocol.Ccs2 && controlToken is null)
        {
            throw new ArgumentNullException(nameof(controlToken), "CCS2 commands need the control token of the PIN.");
        }
    }

    private static string? GetText(JsonElement entry, string name) =>
        entry.GetString(name) is { } text && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static string CreateCcs2Command(string command) => BluelinkJson.Write(writer => writer.WriteString("command", command));

    /// <returns>The message ID of the command.</returns>
    private async Task<string> SendCommandAsync(string url, BluelinkVehicleRequest request, string body, string? controlToken, CancellationToken cancellationToken)
    {
        var root = await SendAsync(HttpMethod.Post, url, request, body, controlToken, cancellationToken);
        return root.GetString("msgId") is { Length: > 0 } messageId
            ? messageId
            : throw new BluelinkApiException(HttpStatusCode.OK, null, "The command returned no message ID.");
    }

    private Task<JsonElement> SendAsync(HttpMethod method, string url, BluelinkVehicleRequest request, string? body, string? controlToken, CancellationToken cancellationToken, bool waitsForCar = false) =>
        SendAsync(method, url, new CallIdentity(request.AccessToken, request.DeviceId, request.Protocol), body, controlToken, cancellationToken, waitsForCar);

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, CallIdentity identity, string? body, string? controlToken, CancellationToken cancellationToken, bool waitsForCar = false)
    {
        budget.Consume();
        using var message = new HttpRequestMessage(method, url);
        var headers = message.Headers;
        if (controlToken is not null)
        {
            headers.TryAddWithoutValidation("Authorization", "Bearer " + controlToken);
            headers.TryAddWithoutValidation("AuthorizationCCSP", "Bearer " + controlToken);
        }
        else if (identity.AccessToken is not null)
        {
            headers.TryAddWithoutValidation("Authorization", "Bearer " + identity.AccessToken);
        }

        headers.TryAddWithoutValidation("ccsp-service-id", BluelinkAppConstants.ServiceId);
        headers.TryAddWithoutValidation("ccsp-application-id", BluelinkAppConstants.ApplicationId);
        headers.TryAddWithoutValidation("Stamp", BluelinkStamp.Create(timeProvider.GetUtcNow()));
        if (identity.DeviceId is not null)
        {
            headers.TryAddWithoutValidation("ccsp-device-id", identity.DeviceId);
        }

        if (identity.Protocol is { } value)
        {
            headers.TryAddWithoutValidation("Ccuccs2protocolsupport", value == BluelinkProtocol.Ccs2 ? "1" : "0");
        }

        headers.TryAddWithoutValidation("User-Agent", BluelinkAppConstants.VehicleApiUserAgent);
        if (body is not null)
        {
            message.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await httpClient.SendWithTimeoutAsync(timeProvider, message, waitsForCar, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return BluelinkVehicleApiResponses.ParseEnvelope(response.StatusCode, text);
    }
}
