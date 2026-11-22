using System.Globalization;
using System.Text;
using Namotion.Devices.Hyundai.Client;
using Namotion.Devices.Hyundai.Mapping;

namespace Namotion.Devices.Hyundai;

/// <summary>A vehicle found while connecting an account.</summary>
public sealed record BluelinkVehicleSummary(string Vin, string? Nickname, string? Model, string? Type, BluelinkProtocol Protocol)
{
    /// <summary>Whether the integration supports the vehicle; only battery electric vehicles are supported.</summary>
    public bool IsSupported => Type == "EV";

    /// <summary>The last six characters of the VIN.</summary>
    public string VinSuffix => BluelinkValues.VinSuffix(Vin);
}

/// <summary>
/// The result of connecting an account: the credentials and tokens to store and the vehicles found.
/// <see cref="object.ToString"/> leaves out the password, the PIN and the tokens.
/// </summary>
public sealed record BluelinkConnection(
    string Email,
    string Password,
    string Pin,
    string Tokens,
    string DeviceId,
    IReadOnlyList<BluelinkVehicleSummary> Vehicles)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(CultureInfo.InvariantCulture, $"Email = {Email}, Vehicles = {Vehicles.Count}");
        return true;
    }
}

/// <summary>
/// Connects a Bluelink account for the setup: registers a device, signs in and lists the vehicles. Never reaches a
/// vehicle. Checks the PIN only when a PIN is given and the account has a CCS2 electric vehicle, because five wrong
/// PINs lock PIN entry. The email and the PIN are trimmed.
/// </summary>
public static class BluelinkAccountConnector
{
    /// <exception cref="BluelinkReauthenticationRequiredException">Hyundai rejected the credentials or asks to accept new terms.</exception>
    /// <exception cref="BluelinkPinRejectedException">Hyundai rejected the PIN.</exception>
    /// <exception cref="BluelinkApiException">Hyundai failed otherwise.</exception>
    /// <exception cref="HttpRequestException">The network failed.</exception>
    /// <exception cref="OperationCanceledException">The call was canceled, or an HTTP request timed out.</exception>
    public static async Task<BluelinkConnection> ConnectAsync(string email, string password, string pin, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var httpClient = BluelinkHttp.CreateClient();
        return await ConnectAsync(httpClient, email, password, pin, timeProvider, cancellationToken);
    }

    internal static async Task<BluelinkConnection> ConnectAsync(HttpClient httpClient, string email, string password, string pin, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        email = email.Trim();
        pin = pin.Trim();

        // The few calls of a connect are not counted; the account counts its own calls from its first poll.
        var apiClient = new BluelinkVehicleApiClient(httpClient, timeProvider, new BluelinkRequestBudget(timeProvider, () => int.MaxValue));
        var authClient = new BluelinkAuthClient(httpClient, timeProvider);

        string deviceId;
        try
        {
            deviceId = await apiClient.RegisterDeviceAsync(cancellationToken);
        }
        catch (BluelinkApiException)
        {
            // As in hyundai_kia_connect_api, the login works with a random device identity when the registration fails.
            deviceId = Guid.NewGuid().ToString();
        }

        var cciTokens = await authClient.LoginAsync(email, password, deviceId, cancellationToken);
        var ccsToken = await authClient.ExchangeCcsAsync(cciTokens.Tokens, deviceId, cancellationToken);
        var vehicles = await apiClient.GetVehiclesAsync(ccsToken.AccessToken, deviceId, cancellationToken);

        if (pin.Length > 0 && vehicles.Any(vehicle => vehicle.IsElectric && vehicle.Protocol == BluelinkProtocol.Ccs2))
        {
            await apiClient.GetControlTokenAsync(ccsToken.AccessToken, deviceId, pin, cancellationToken);
        }

        return new BluelinkConnection(email, password, pin, cciTokens.Tokens.Serialize(), deviceId,
            vehicles.Select(vehicle => new BluelinkVehicleSummary(vehicle.Vin, NullWhenBlank(vehicle.Nickname), NullWhenBlank(vehicle.ModelName), vehicle.Type, vehicle.Protocol)).ToArray());
    }

    private static string? NullWhenBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
