using System.Text.Json;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;

namespace Namotion.Devices.Tesla.Tests.Testing;

internal static class FixtureLoader
{
    /// <summary>The timestamp of the sections in vehicle-data.json, in Unix milliseconds.</summary>
    public const long FixtureTimestamp = 1795293677353;

    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>The vehicle_data response of vehicle-data.json.</summary>
    public static TeslaVehicleDataDto LoadVehicleData() =>
        JsonSerializer.Deserialize(Load("vehicle-data.json"), TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)!.Response!;
}
