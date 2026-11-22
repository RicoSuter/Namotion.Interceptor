using System.Globalization;
using System.Text;
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
