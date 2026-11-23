using CarServer;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Namotion.Devices.Tesla.Tests.Testing;

/// <summary>
/// Builds the base64 protobuf snapshot that /api/1/products returns in cached_data.
/// </summary>
internal static class VehicleDataBuilder
{
    public static VehicleData Create(DateTimeOffset timestamp)
    {
        var protoTimestamp = Timestamp.FromDateTimeOffset(timestamp);
        return new VehicleData
        {
            ChargeState = new ChargeState
            {
                BatteryLevel = 65,
                UsableBatteryLevel = 64,
                ChargeLimitSoc = 80,
                BatteryRange = 200f,
                ChargingState = new ChargeState.Types.ChargingState { Charging = new CarServer.Void() },
                ChargerPower = 11,
                MinutesToChargeLimit = 90,
                Timestamp = protoTimestamp
            },
            ClimateState = new ClimateState
            {
                InsideTempCelsius = 20.5f,
                OutsideTempCelsius = 10f,
                IsClimateOn = true,
                Timestamp = protoTimestamp
            },
            DriveState = new DriveState
            {
                ShiftState = new ShiftState { D = new CarServer.Void() },
                SpeedFloat = 50f,
                Power = 20,
                Timestamp = protoTimestamp
            },
            ClosuresState = new ClosuresState { Locked = false, IsUserPresent = false, Timestamp = protoTimestamp },
            LocationState = new LocationState { Latitude = 47.5f, Longitude = 8.5f, Heading = 90, Timestamp = protoTimestamp },
            TirePressureState = new TirePressureState { TpmsPressureFl = 2.8f, Timestamp = protoTimestamp }
        };
    }

    public static string ToBase64(VehicleData data) => data.ToByteString().ToBase64();
}
