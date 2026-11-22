using CarServer;
using Google.Protobuf.WellKnownTypes;
using HomeBlaze.Abstractions.Devices.Locks;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Tests.Testing;
using static Namotion.Devices.Tesla.Tests.Testing.FixtureLoader;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaVehicleBodyTests
{
    private static TeslaVehicleStateDto LoadVehicleState() => LoadVehicleData().VehicleState!;

    private static TeslaVehicleBody CreateFromFixture()
    {
        var body = new TeslaVehicleBody();
        body.Update(LoadVehicleState());
        return body;
    }

    private static VehicleData CreateSnapshot(long offsetMilliseconds) =>
        VehicleDataBuilder.Create(DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp + offsetMilliseconds));

    // The protobuf state that equals the fixture, with the fractions of a second and the float imprecision of the real snapshot.
    private static (ClosuresState Closures, TirePressureState Tires) CreateSnapshotOfFixture()
    {
        var timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp));
        var closures = new ClosuresState
        {
            Locked = true,
            DoorOpenDriverFront = false,
            DoorOpenDriverRear = false,
            DoorOpenPassengerFront = false,
            DoorOpenPassengerRear = false,
            DoorOpenTrunkFront = false,
            DoorOpenTrunkRear = false,
            WindowOpenDriverFront = false,
            WindowOpenPassengerFront = false,
            WindowOpenDriverRear = false,
            WindowOpenPassengerRear = false,
            IsUserPresent = false,
            RemoteStart = false,
            ValetMode = false,
            ValetPinNeeded = true,
            SentryModeAvailable = true,
            Timestamp = timestamp
        };

        var tires = new TirePressureState
        {
            TpmsPressureFl = 2.9f,
            TpmsPressureFr = 2.85f,
            TpmsPressureRl = 2.975f,
            TpmsPressureRr = 3f,
            TpmsRcpFrontValue = 2.9f,
            TpmsRcpRearValue = 2.9f,
            TpmsSoftWarningFl = false,
            TpmsSoftWarningFr = false,
            TpmsSoftWarningRl = false,
            TpmsSoftWarningRr = false,
            TpmsHardWarningFl = false,
            TpmsHardWarningFr = false,
            TpmsHardWarningRl = false,
            TpmsHardWarningRr = false,
            TpmsLastSeenPressureTimeFl = new Timestamp { Seconds = 1795111581, Nanos = 775_000_000 },
            TpmsLastSeenPressureTimeFr = new Timestamp { Seconds = 1795111581, Nanos = 775_000_000 },
            TpmsLastSeenPressureTimeRl = new Timestamp { Seconds = 1795111581, Nanos = 775_000_000 },
            TpmsLastSeenPressureTimeRr = new Timestamp { Seconds = 1795111551, Nanos = 775_000_000 },
            Timestamp = timestamp
        };

        return (closures, tires);
    }

    [Fact]
    public void WhenFixtureIsApplied_ThenClosuresAndTiresAreMapped()
    {
        // Act
        var body = CreateFromFixture();

        // Assert
        Assert.Equal(DoorLockState.Locked, body.LockState);
        Assert.False(body.IsAnyDoorOpen);
        Assert.False(body.IsAnyWindowOpen);
        Assert.False(body.IsFrunkOpen);
        Assert.False(body.IsTrunkOpen);
        Assert.False(body.IsSentryModeOn);
        Assert.Equal("Recording", body.DashcamState);
        Assert.Equal(2.9m, body.FrontLeftTire.Pressure);
        Assert.Equal(2.85m, body.FrontRightTire.Pressure);
        Assert.Equal(2.975m, body.RearLeftTire.Pressure);
        Assert.Equal(2.9m, body.FrontLeftTire.RecommendedPressure);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795111551), body.RearRightTire.LastSeen);
        Assert.False(body.RearLeftTire.HasHardWarning);
    }

    [Fact]
    public void WhenFixtureIsApplied_ThenRemainingFieldsAreMapped()
    {
        // Act
        var body = CreateFromFixture();

        // Assert
        Assert.False(body.IsDriverFrontDoorOpen);
        Assert.False(body.IsPassengerRearWindowOpen);
        Assert.False(body.IsUserPresent);
        Assert.True(body.IsSentryModeAvailable);
        Assert.False(body.IsValetModeOn);
        Assert.True(body.IsValetPinNeeded);
        Assert.False(body.IsRemoteStartActive);
        Assert.True(body.IsRemoteStartEnabled);
        Assert.True(body.IsRemoteStartSupported);
        Assert.False(body.IsInServiceMode);
        Assert.False(body.IsInServiceModePlus);
        Assert.True(body.IsDashcamClipSaveAvailable);
        Assert.Equal(0, body.CenterDisplayState);
        Assert.True(body.IsWebcamAvailable);
        Assert.Equal("unavailable", body.AutoparkState);
        Assert.Equal(0, body.SantaMode);
        Assert.Equal(0, body.SelfTestProgress);
        Assert.False(body.IsSelfTestRequested);
        Assert.False(body.FrontRightTire.HasSoftWarning);
        Assert.Equal(2.9m, body.RearRightTire.RecommendedPressure);
    }

    [Fact]
    public void WhenAClosureIsOpenInJson_ThenTheDerivedFlagsAreTrue()
    {
        // Arrange
        var body = new TeslaVehicleBody();
        var dto = new TeslaVehicleStateDto { Df = 0, Pr = 1, FdWindow = 2, Timestamp = FixtureTimestamp };

        // Act
        body.Update(dto);

        // Assert
        Assert.True(body.IsAnyDoorOpen);
        Assert.True(body.IsAnyWindowOpen);
        Assert.True(body.IsPassengerRearDoorOpen);
        Assert.Null(body.IsFrunkOpen);
    }

    [Fact]
    public void WhenNoClosureIsReported_ThenTheDerivedFlagsAreNull()
    {
        // Arrange
        var body = new TeslaVehicleBody();

        // Act & Assert
        Assert.Null(body.IsAnyDoorOpen);
        Assert.Null(body.IsAnyWindowOpen);
    }

    [Fact]
    public void WhenSnapshotIsNewer_ThenLockAndTiresUpdate()
    {
        // Arrange
        var body = CreateFromFixture();
        var snapshot = CreateSnapshot(60_000);

        // Act
        body.Update(snapshot.ClosuresState);
        body.Update(snapshot.TirePressureState);

        // Assert
        Assert.Equal(DoorLockState.Unlocked, body.LockState);
        Assert.Equal(2.8m, body.FrontLeftTire.Pressure);
        Assert.Equal(2.85m, body.FrontRightTire.Pressure); // absent in the snapshot, unchanged
        Assert.True(body.IsSentryModeAvailable); // absent in the snapshot, unchanged
        Assert.Equal("Recording", body.DashcamState); // JSON only, unchanged
    }

    [Fact]
    public void WhenSnapshotIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var body = CreateFromFixture();
        var snapshot = CreateSnapshot(-60_000);

        // Act
        body.Update(snapshot.ClosuresState);
        body.Update(snapshot.TirePressureState);

        // Assert
        Assert.Equal(DoorLockState.Locked, body.LockState);
        Assert.Equal(2.9m, body.FrontLeftTire.Pressure);
    }

    [Fact]
    public void WhenSnapshotHasEqualTimestamp_ThenItApplies()
    {
        // Arrange
        var body = CreateFromFixture();
        var snapshot = CreateSnapshot(0);

        // Act
        body.Update(snapshot.ClosuresState);
        body.Update(snapshot.TirePressureState);

        // Assert
        Assert.Equal(DoorLockState.Unlocked, body.LockState);
        Assert.Equal(2.8m, body.FrontLeftTire.Pressure);
    }

    [Fact]
    public void WhenJsonIsOlderThanAppliedSnapshot_ThenItIsIgnored()
    {
        // Arrange
        var body = new TeslaVehicleBody();
        var snapshot = CreateSnapshot(60_000);
        body.Update(snapshot.ClosuresState);
        body.Update(snapshot.TirePressureState);

        // Act
        body.Update(LoadVehicleState());

        // Assert
        Assert.Equal(DoorLockState.Unlocked, body.LockState);
        Assert.Equal(2.8m, body.FrontLeftTire.Pressure);
    }

    [Fact]
    public void WhenSnapshotReportsAnOpenDoorAndWarning_ThenTheyAreMapped()
    {
        // Arrange
        var body = CreateFromFixture();
        var snapshot = CreateSnapshot(60_000);
        snapshot.ClosuresState.DoorOpenTrunkRear = true;
        snapshot.ClosuresState.WindowOpenDriverRear = true;
        snapshot.TirePressureState.TpmsSoftWarningRr = true;
        snapshot.TirePressureState.TpmsHardWarningFr = true;
        snapshot.TirePressureState.TpmsPressureRl = float.NaN;

        // Act
        body.Update(snapshot.ClosuresState);
        body.Update(snapshot.TirePressureState);

        // Assert
        Assert.True(body.IsTrunkOpen);
        Assert.True(body.IsAnyWindowOpen);
        Assert.True(body.RearRightTire.HasSoftWarning);
        Assert.True(body.FrontRightTire.HasHardWarning);
        Assert.Null(body.RearLeftTire.Pressure);
    }

    [Fact]
    public void WhenSnapshotEqualsTheJsonState_ThenBothSourcesGiveEqualValues()
    {
        // Arrange
        var json = CreateFromFixture();
        var (closures, tires) = CreateSnapshotOfFixture();
        var both = CreateFromFixture();
        var protobufOnly = new TeslaVehicleBody();

        // Act
        both.Update(closures);
        both.Update(tires);
        protobufOnly.Update(closures);
        protobufOnly.Update(tires);

        // Assert
        foreach (var body in new[] { both, protobufOnly })
        {
            Assert.Equal(json.LockState, body.LockState);
            Assert.Equal(json.IsAnyDoorOpen, body.IsAnyDoorOpen);
            Assert.Equal(json.IsValetPinNeeded, body.IsValetPinNeeded);
            Assert.Equal(json.IsRemoteStartActive, body.IsRemoteStartActive);
            AssertTiresEqual(json.FrontLeftTire, body.FrontLeftTire);
            AssertTiresEqual(json.FrontRightTire, body.FrontRightTire);
            AssertTiresEqual(json.RearLeftTire, body.RearLeftTire);
            AssertTiresEqual(json.RearRightTire, body.RearRightTire);
        }
    }

    private static void AssertTiresEqual(TeslaTire expected, TeslaTire actual)
    {
        Assert.Equal(expected.Pressure, actual.Pressure);
        Assert.Equal(expected.RecommendedPressure, actual.RecommendedPressure);
        Assert.Equal(expected.HasSoftWarning, actual.HasSoftWarning);
        Assert.Equal(expected.HasHardWarning, actual.HasHardWarning);
        Assert.Equal(expected.LastSeen, actual.LastSeen);
    }
}
