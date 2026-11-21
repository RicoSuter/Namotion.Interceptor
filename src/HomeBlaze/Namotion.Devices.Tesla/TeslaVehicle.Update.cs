using CarServer;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;

namespace Namotion.Devices.Tesla;

public partial class TeslaVehicle
{
    private static readonly Func<TeslaVehicleDataDto, long?>[] VehicleDataTimestamps =
    [
        data => data.ChargeState?.Timestamp,
        data => data.ClimateState?.Timestamp,
        data => data.DriveState?.Timestamp,
        data => data.GuiSettings?.Timestamp,
        data => data.VehicleConfig?.Timestamp,
        data => data.VehicleState?.Timestamp
    ];

    private static readonly Func<VehicleData, Timestamp?>[] SnapshotTimestamps =
    [
        snapshot => snapshot.ChargeState?.Timestamp,
        snapshot => snapshot.ClimateState?.Timestamp,
        snapshot => snapshot.DriveState?.Timestamp,
        snapshot => snapshot.LocationState?.Timestamp,
        snapshot => snapshot.ClosuresState?.Timestamp,
        snapshot => snapshot.TirePressureState?.Timestamp,
        snapshot => snapshot.MediaState?.Timestamp,
        snapshot => snapshot.MediaDetailState?.Timestamp,
        snapshot => snapshot.SoftwareUpdateState?.Timestamp
    ];

    /// <summary>
    /// Applies an entry of /api/1/products: state, names and the cached snapshot. Never wakes the vehicle.
    /// </summary>
    internal void ApplyProduct(TeslaProductDto product)
    {
        VehicleState = TeslaValues.Enum<TeslaVehicleState>(product.State, _logger) ?? TeslaVehicleState.Unknown;
        DisplayName = TeslaValues.Text(product.DisplayName);
        CommandSigning = TeslaValues.Enum<TeslaCommandSigning>(product.CommandSigning, _logger);
        IsInService = product.InService;

        if (TeslaValues.Text(product.CachedData) is { } cachedData)
        {
            ApplyCachedData(cachedData);
        }
    }

    /// <summary>
    /// Applies a vehicle_data response of an awake vehicle.
    /// </summary>
    internal void ApplyVehicleData(TeslaVehicleDataDto data)
    {
        VehicleState = TeslaValues.Enum<TeslaVehicleState>(data.State, _logger) ?? VehicleState;

        if (data.ChargeState is { } charge) Charge.Update(charge);
        if (data.ClimateState is { } climate) Climate.Update(climate);
        if (data.DriveState is { } drive) Drive.Update(drive);
        if (data.GuiSettings is { } guiSettings) DisplaySettings.Update(guiSettings);
        if (data.VehicleConfig is { } config) Specification.Update(config);
        if (data.VehicleState is { } state) ApplyVehicleState(state);

        UpdateLastUpdatedFromVehicleData(data);
    }

    private void UpdateLastUpdatedFromVehicleData(TeslaVehicleDataDto data)
    {
        long? newest = null;
        foreach (var getTimestamp in VehicleDataTimestamps)
        {
            if (getTimestamp(data) is { } timestamp && (newest is null || timestamp > newest))
            {
                newest = timestamp;
            }
        }

        UpdateLastUpdated(TeslaValues.FromUnixMilliseconds(newest) ?? _timeProvider.GetUtcNow());
    }

    private void ApplyVehicleState(TeslaVehicleStateDto state)
    {
        var timestamp = TeslaValues.FromUnixMilliseconds(state.Timestamp);

        Body.Update(state);
        Media.Update(state);
        if (state.SoftwareUpdate is { } update) SoftwareUpdate.Update(update, timestamp);
        if (state.SpeedLimitMode is { } speedLimit) SpeedLimit.Update(speedLimit, timestamp);

        Drive.UpdateHomelinkNearby(state.HomelinkNearby, timestamp);
        VehicleName = TeslaValues.Text(state.VehicleName);
        SoftwareVersion = TeslaValues.Text(state.CarVersion);

        // The snapshot reports hundredths of a mile, so the JSON miles are rounded to match.
        if (_odometerClock.TryAdvance(timestamp))
        {
            TotalDrivenDistance = TeslaValues.MilesToKilometers(state.Odometer is { } miles ? Math.Round(miles, 2, MidpointRounding.AwayFromZero) : null);
        }
    }

    private void ApplyCachedData(string cachedData)
    {
        VehicleData snapshot;
        try
        {
            snapshot = VehicleData.Parser.ParseFrom(Convert.FromBase64String(cachedData));
        }
        catch (Exception exception) when (exception is FormatException or InvalidProtocolBufferException)
        {
            // Every poll returns the same cached data, so only the first failure in a row is a warning.
            if (_hasWarnedAboutCachedData)
            {
                _logger.LogDebug(exception, "The cached data of Tesla vehicle {Vin} could not be read.", Vin);
            }
            else
            {
                _logger.LogWarning(exception, "The cached data of Tesla vehicle {Vin} could not be read.", Vin);
                _hasWarnedAboutCachedData = true;
            }

            return;
        }

        _hasWarnedAboutCachedData = false;
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(VehicleData snapshot)
    {
        // A section that the snapshot does not carry is null.
        if (snapshot.ChargeState is { } charge) Charge.Update(charge);
        if (snapshot.ClimateState is { } climate) Climate.Update(climate);
        if (snapshot.DriveState is { } drive) ApplyDriveSnapshot(drive);
        if (snapshot.LocationState is { } location) Drive.Update(location);
        if (snapshot.ClosuresState is { } closures) ApplyClosuresSnapshot(closures);
        if (snapshot.TirePressureState is { } tires) Body.Update(tires);
        if (snapshot.MediaState is { } media) Media.Update(media);
        if (snapshot.MediaDetailState is { } mediaDetail) Media.Update(mediaDetail);
        if (snapshot.SoftwareUpdateState is { } update) SoftwareUpdate.Update(update);

        UpdateLastUpdatedFromSnapshot(snapshot);
    }

    private void ApplyDriveSnapshot(DriveState drive)
    {
        Drive.Update(drive);

        if (drive.OptionalOdometerInHundredthsOfAMileCase != DriveState.OptionalOdometerInHundredthsOfAMileOneofCase.None &&
            _odometerClock.TryAdvance(drive.Timestamp?.ToDateTimeOffset()))
        {
            TotalDrivenDistance = TeslaValues.MilesToKilometers(drive.OdometerInHundredthsOfAMile / 100m);
        }
    }

    private void ApplyClosuresSnapshot(ClosuresState closures)
    {
        Body.Update(closures);

        if (closures.SpeedLimitMode is { } speedLimit)
        {
            SpeedLimit.Update(speedLimit, closures.Timestamp?.ToDateTimeOffset());
        }
    }

    private void UpdateLastUpdatedFromSnapshot(VehicleData snapshot)
    {
        DateTimeOffset? newest = null;
        foreach (var getTimestamp in SnapshotTimestamps)
        {
            if (getTimestamp(snapshot)?.ToDateTimeOffset() is { } timestamp && (newest is null || timestamp > newest))
            {
                newest = timestamp;
            }
        }

        if (newest is { } value)
        {
            UpdateLastUpdated(value);
        }
    }

    private void UpdateLastUpdated(DateTimeOffset timestamp)
    {
        if (LastUpdated is null || timestamp > LastUpdated)
        {
            LastUpdated = timestamp;
        }
    }
}
