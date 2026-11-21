using System.Text.Json;
using CarServer;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Tesla.Client;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Tests.Testing;

namespace Namotion.Devices.Tesla.Tests;

public class TeslaVehicleDetailsTests
{
    private const long FixtureTimestamp = 1795293677353;

    private static TeslaVehicleDataDto LoadFixture() =>
        JsonSerializer.Deserialize(FixtureLoader.Load("vehicle-data.json"), TeslaJsonContext.Default.TeslaResponseTeslaVehicleDataDto)!.Response!;

    private static Timestamp Timestamp(long offsetMilliseconds) =>
        Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp + offsetMilliseconds));

    private static TeslaMedia CreateMediaFromFixture()
    {
        var media = new TeslaMedia(NullLogger.Instance);
        media.Update(LoadFixture().VehicleState!);
        return media;
    }

    private static TeslaSoftwareUpdate CreateSoftwareUpdateFromFixture()
    {
        var update = new TeslaSoftwareUpdate(NullLogger.Instance);
        update.Update(LoadFixture().VehicleState!.SoftwareUpdate!);
        return update;
    }

    [Fact]
    public void WhenMediaFixtureIsApplied_ThenTrackAndVolumeAreMapped()
    {
        // Act
        var media = CreateMediaFromFixture();

        // Assert
        Assert.Equal(TeslaMediaPlaybackStatus.Paused, media.PlaybackStatus);
        Assert.Equal("Spotify", media.Source);
        Assert.Null(media.CurrentTrackTitle); // empty in the fixture
        Assert.Equal(TimeSpan.FromMilliseconds(3755000), media.CurrentTrackDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(2329000), media.CurrentTrackPosition);
        Assert.Equal(Math.Round(5.6667m / 10.333333m, 4), Math.Round(media.Volume!.Value, 4)); // 0..1 of the maximum
        Assert.True(media.IsRemoteControlEnabled);
        Assert.Null(media.IsMuted);
    }

    [Fact]
    public void WhenMediaFixtureIsApplied_ThenRemainingFieldsAreMapped()
    {
        // Act
        var media = CreateMediaFromFixture();

        // Assert
        Assert.Null(media.CurrentTrackArtist);
        Assert.Null(media.CurrentTrackAlbum);
        Assert.Null(media.Station);
        Assert.Equal(5.6667m, media.AudioVolume);
        Assert.Equal(10.33333m, media.AudioVolumeMaximum);
        Assert.Equal(0.3333330m, media.AudioVolumeIncrement);
        Assert.Null(media.CurrentTrackImageUri);
        Assert.Null(media.CurrentTrackUri);
    }

    [Fact]
    public void WhenTheMaximumVolumeIsMissing_ThenTheVolumeFractionIsNull()
    {
        // Arrange
        var media = new TeslaMedia(NullLogger.Instance);
        var dto = new TeslaVehicleStateDto { MediaInfo = new TeslaMediaInfoDto { AudioVolume = 3m, AudioVolumeMax = 0m }, Timestamp = FixtureTimestamp };

        // Act
        media.Update(dto);

        // Assert
        Assert.Equal(3m, media.AudioVolume);
        Assert.Null(media.Volume);
    }

    [Fact]
    public void WhenAMediaSnapshotIsNewer_ThenItUpdatesAndAbsentFieldsStay()
    {
        // Arrange
        var media = CreateMediaFromFixture();
        var state = new MediaState
        {
            NowPlayingTitle = "Test Title",
            NowPlayingArtist = "Test Artist",
            AudioVolume = 7.5f,
            MediaPlaybackStatus = MediaPlaybackStatus.Playing,
            Timestamp = Timestamp(60_000)
        };
        var detail = new MediaDetailState
        {
            NowPlayingAlbum = "Test Album",
            NowPlayingStation = "Test Station",
            NowPlayingDuration = 200_000,
            NowPlayingElapsed = 5_000,
            NowPlayingSourceString = "Radio",
            Timestamp = Timestamp(60_000)
        };

        // Act
        media.Update(state);
        media.Update(detail);

        // Assert
        Assert.Equal("Test Title", media.CurrentTrackTitle);
        Assert.Equal("Test Artist", media.CurrentTrackArtist);
        Assert.Equal("Test Album", media.CurrentTrackAlbum);
        Assert.Equal("Test Station", media.Station);
        Assert.Equal("Radio", media.Source);
        Assert.Equal(TimeSpan.FromSeconds(200), media.CurrentTrackDuration);
        Assert.Equal(TimeSpan.FromSeconds(5), media.CurrentTrackPosition);
        Assert.Equal(TeslaMediaPlaybackStatus.Playing, media.PlaybackStatus);
        Assert.Equal(7.5m, media.AudioVolume);
        Assert.Equal(10.33333m, media.AudioVolumeMaximum); // absent in the snapshot, unchanged
        Assert.True(media.IsRemoteControlEnabled); // absent in the snapshot, unchanged
    }

    [Fact]
    public void WhenAMediaSnapshotIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var media = CreateMediaFromFixture();

        // Act
        media.Update(new MediaState { NowPlayingTitle = "Test Title", Timestamp = Timestamp(-60_000) });
        media.Update(new MediaDetailState { NowPlayingAlbum = "Test Album", Timestamp = Timestamp(-60_000) });

        // Assert
        Assert.Null(media.CurrentTrackTitle);
        Assert.Null(media.CurrentTrackAlbum);
    }

    [Fact]
    public void WhenAMediaSnapshotHasEqualTimestamp_ThenItApplies()
    {
        // Arrange
        var media = CreateMediaFromFixture();

        // Act
        media.Update(new MediaState { NowPlayingTitle = "Test Title", Timestamp = Timestamp(0) });

        // Assert
        Assert.Equal("Test Title", media.CurrentTrackTitle);
    }

    [Fact]
    public void WhenAMediaSnapshotReportsAnUnknownStatusOrZeroDuration_ThenStatusStaysAndDurationIsNull()
    {
        // Arrange
        var media = CreateMediaFromFixture();

        // Act
        media.Update(new MediaState { MediaPlaybackStatus = (MediaPlaybackStatus)99, Timestamp = Timestamp(60_000) });
        media.Update(new MediaDetailState { NowPlayingDuration = 0, Timestamp = Timestamp(60_000) });

        // Assert
        Assert.Equal(TeslaMediaPlaybackStatus.Paused, media.PlaybackStatus);
        Assert.Null(media.CurrentTrackDuration);
    }

    [Fact]
    public void WhenAMediaSnapshotEqualsTheJsonState_ThenBothSourcesGiveEqualValues()
    {
        // Arrange
        var json = CreateMediaFromFixture();
        var both = CreateMediaFromFixture();
        var protobufOnly = new TeslaMedia(NullLogger.Instance);
        var state = new MediaState
        {
            RemoteControlEnabled = true,
            NowPlayingTitle = "",
            NowPlayingArtist = "",
            AudioVolume = 5.6667f,
            AudioVolumeIncrement = 0.333333f,
            AudioVolumeMax = 10.333333f,
            MediaPlaybackStatus = MediaPlaybackStatus.Paused,
            Timestamp = Timestamp(0)
        };
        var detail = new MediaDetailState
        {
            NowPlayingDuration = 3755000,
            NowPlayingElapsed = 2329000,
            NowPlayingSourceString = "Spotify",
            NowPlayingAlbum = "",
            NowPlayingStation = "",
            Timestamp = Timestamp(0)
        };

        // Act
        both.Update(state);
        both.Update(detail);
        protobufOnly.Update(state);
        protobufOnly.Update(detail);

        // Assert
        foreach (var media in new[] { both, protobufOnly })
        {
            Assert.Equal(json.PlaybackStatus, media.PlaybackStatus);
            Assert.Equal(json.Source, media.Source);
            Assert.Equal(json.CurrentTrackTitle, media.CurrentTrackTitle);
            Assert.Equal(json.CurrentTrackAlbum, media.CurrentTrackAlbum);
            Assert.Equal(json.Station, media.Station);
            Assert.Equal(json.CurrentTrackDuration, media.CurrentTrackDuration);
            Assert.Equal(json.CurrentTrackPosition, media.CurrentTrackPosition);
            Assert.Equal(json.AudioVolume, media.AudioVolume);
            Assert.Equal(json.AudioVolumeMaximum, media.AudioVolumeMaximum);
            Assert.Equal(json.AudioVolumeIncrement, media.AudioVolumeIncrement);
            Assert.Equal(json.Volume, media.Volume);
            Assert.Equal(json.IsRemoteControlEnabled, media.IsRemoteControlEnabled);
        }
    }

    [Fact]
    public void WhenSoftwareUpdateHasNoVersion_ThenNoUpdateIsAvailable()
    {
        // Act
        var update = CreateSoftwareUpdateFromFixture();

        // Assert
        Assert.Null(update.Version);   // " "
        Assert.Null(update.Status);    // ""
        Assert.Equal(TimeSpan.FromSeconds(2700), update.ExpectedDuration);
        Assert.Equal(0m, update.DownloadProgress);
        Assert.Equal(0.01m, update.InstallProgress);
    }

    [Theory]
    [InlineData("available", TeslaSoftwareUpdateStatus.Available)]
    [InlineData("downloading_wifi_wait", TeslaSoftwareUpdateStatus.DownloadingWifiWait)]
    [InlineData("installing", TeslaSoftwareUpdateStatus.Installing)]
    public void WhenSoftwareUpdateStatusIsReportedInJson_ThenItIsParsed(string status, TeslaSoftwareUpdateStatus expected)
    {
        // Arrange
        var update = new TeslaSoftwareUpdate(NullLogger.Instance);

        // Act
        update.Update(new TeslaSoftwareUpdateDto { Status = status, Version = "2026.40.1 abc" });

        // Assert
        Assert.Equal(expected, update.Status);
        Assert.Equal("2026.40.1 abc", update.Version);
    }

    [Fact]
    public void WhenASoftwareUpdateSnapshotIsNewer_ThenItUpdatesAndAbsentFieldsStay()
    {
        // Arrange
        var update = CreateSoftwareUpdateFromFixture();
        var snapshot = new SoftwareUpdateState
        {
            Status = new SoftwareUpdateState.Types.SoftwareUpdateStatus { Downloading = new CarServer.Void() },
            Version = "2026.40.1 abc",
            DownloadPerc = 42,
            Timestamp = Timestamp(60_000)
        };

        // Act
        update.Update(snapshot);

        // Assert
        Assert.Equal(TeslaSoftwareUpdateStatus.Downloading, update.Status);
        Assert.Equal("2026.40.1 abc", update.Version);
        Assert.Equal(0.42m, update.DownloadProgress);
        Assert.Equal(0.01m, update.InstallProgress); // absent in the snapshot, unchanged
    }

    [Fact]
    public void WhenASoftwareUpdateSnapshotIsOlderOrUnknown_ThenValuesStay()
    {
        // Arrange
        var update = new TeslaSoftwareUpdate(NullLogger.Instance);
        update.Update(new TeslaSoftwareUpdateDto { Status = "available", Version = "2026.40.1 abc" }, DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp));

        // Act
        update.Update(new SoftwareUpdateState { Version = "other", Timestamp = Timestamp(-60_000) });
        update.Update(new SoftwareUpdateState { Status = new SoftwareUpdateState.Types.SoftwareUpdateStatus { Unknown = new CarServer.Void() }, Timestamp = Timestamp(60_000) });

        // Assert
        Assert.Equal("2026.40.1 abc", update.Version);
        Assert.Equal(TeslaSoftwareUpdateStatus.Available, update.Status);
    }

    [Fact]
    public void WhenASoftwareUpdateSnapshotEqualsTheJsonState_ThenBothSourcesGiveEqualValues()
    {
        // Arrange
        var json = CreateSoftwareUpdateFromFixture();
        var protobufOnly = new TeslaSoftwareUpdate(NullLogger.Instance);
        var snapshot = new SoftwareUpdateState
        {
            Status = new SoftwareUpdateState.Types.SoftwareUpdateStatus { Unknown = new CarServer.Void() },
            ExpectedDurationSec = 2700,
            InstallPerc = 1,
            DownloadPerc = 0,
            Version = " ",
            Timestamp = Timestamp(0)
        };

        // Act
        protobufOnly.Update(snapshot);

        // Assert
        Assert.Equal(json.Status, protobufOnly.Status);
        Assert.Equal(json.Version, protobufOnly.Version);
        Assert.Equal(json.DownloadProgress, protobufOnly.DownloadProgress);
        Assert.Equal(json.InstallProgress, protobufOnly.InstallProgress);
        Assert.Equal(json.ExpectedDuration, protobufOnly.ExpectedDuration);
    }

    [Fact]
    public void WhenSpeedLimitIsApplied_ThenKilometersPerHourAreReturned()
    {
        // Arrange
        var speedLimit = new TeslaSpeedLimitMode();

        // Act
        speedLimit.Update(LoadFixture().VehicleState!.SpeedLimitMode!);

        // Assert
        Assert.False(speedLimit.IsActive);
        Assert.Equal(Math.Round(87.0m * 1.609344m, 3), Math.Round(speedLimit.CurrentLimit!.Value, 3));
        Assert.Equal(50m * 1.609344m, speedLimit.MinimumLimit);
        Assert.Equal(120m * 1.609344m, speedLimit.MaximumLimit);
        Assert.False(speedLimit.IsPinCodeSet);
    }

    [Fact]
    public void WhenASpeedLimitSnapshotEqualsTheJsonState_ThenBothSourcesGiveEqualValues()
    {
        // Arrange
        var json = new TeslaSpeedLimitMode();
        json.Update(LoadFixture().VehicleState!.SpeedLimitMode!, DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp));
        var protobufOnly = new TeslaSpeedLimitMode();
        var snapshot = new SpeedLimitMode { Active = false, PinCodeSet = false, CurrentLimitMph = 87f, MinLimitMph = 50f, MaxLimitMph = 120f };

        // Act
        protobufOnly.Update(snapshot, DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp));

        // Assert
        Assert.Equal(json.IsActive, protobufOnly.IsActive);
        Assert.Equal(json.CurrentLimit, protobufOnly.CurrentLimit);
        Assert.Equal(json.MinimumLimit, protobufOnly.MinimumLimit);
        Assert.Equal(json.MaximumLimit, protobufOnly.MaximumLimit);
        Assert.Equal(json.IsPinCodeSet, protobufOnly.IsPinCodeSet);
    }

    [Fact]
    public void WhenASpeedLimitSnapshotIsOlderOrPartial_ThenOnlyNewerPresentFieldsApply()
    {
        // Arrange
        var speedLimit = new TeslaSpeedLimitMode();
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(FixtureTimestamp);
        speedLimit.Update(LoadFixture().VehicleState!.SpeedLimitMode!, timestamp);

        // Act
        speedLimit.Update(new SpeedLimitMode { Active = true }, timestamp.AddMinutes(-1));
        speedLimit.Update(new SpeedLimitMode { PinCodeSet = true }, timestamp.AddMinutes(1));

        // Assert
        Assert.False(speedLimit.IsActive);
        Assert.True(speedLimit.IsPinCodeSet);
        Assert.Equal(50m * 1.609344m, speedLimit.MinimumLimit); // absent in the snapshot, unchanged
    }

    [Fact]
    public void WhenSpecificationAndDisplayAreApplied_ThenTextsAreMapped()
    {
        // Arrange
        var specification = new TeslaVehicleSpecification();
        var display = new TeslaDisplaySettings();
        var data = LoadFixture();

        // Act
        specification.Update(data.VehicleConfig!);
        display.Update(data.GuiSettings!);

        // Assert
        Assert.Equal("model3", specification.CarType);
        Assert.Equal("74d", specification.TrimBadging);
        Assert.Equal("RedMulticoat", specification.ExteriorColor);
        Assert.Equal("Glider18", specification.WheelType);
        Assert.True(specification.IsEuVehicle);
        Assert.Null(specification.ExteriorTrimOverride); // ""
        Assert.Equal("km/hr", display.DistanceUnits);
        Assert.True(display.Is24HourTime);
    }

    [Fact]
    public void WhenSpecificationAndDisplayAreApplied_ThenRemainingFieldsAreMapped()
    {
        // Arrange
        var specification = new TeslaVehicleSpecification();
        var display = new TeslaDisplaySettings();
        var data = LoadFixture();

        // Act
        specification.Update(data.VehicleConfig!);
        display.Update(data.GuiSettings!);

        // Assert
        Assert.Equal("base", specification.CarSpecialType);
        Assert.Equal("Black", specification.ExteriorTrim);
        Assert.Equal("None", specification.SpoilerType);
        Assert.Equal("CCS", specification.ChargePortType);
        Assert.Equal("Eu", specification.AuxiliaryParkLamps);
        Assert.Equal(1, specification.RearSeatHeaters);
        Assert.Equal(0, specification.RearSeatType);
        Assert.Null(specification.SeatType);
        Assert.Null(specification.HasSunRoof);
        Assert.Equal(2, specification.KeyVersion);
        Assert.True(specification.HasPowerLiftgate);
        Assert.True(specification.HasPedestrianWarningSpeaker);
        Assert.False(specification.IsRightHandDrive);
        Assert.True(specification.UsesRangeBadging);
        Assert.False(specification.IsSentryPreviewSupported);
        Assert.Equal(TimeSpan.FromHours(1), specification.UtcOffset);
        Assert.Equal("kW", display.ChargeRateUnits);
        Assert.Equal("Rated", display.RangeDisplay);
        Assert.Equal("C", display.TemperatureUnits);
        Assert.Equal("Bar", display.TirePressureUnits);
        Assert.False(display.ShowsRangeUnits);
    }

    [Fact]
    public void WhenSpecificationIsOlder_ThenItIsIgnored()
    {
        // Arrange
        var specification = new TeslaVehicleSpecification();
        var data = LoadFixture();
        specification.Update(data.VehicleConfig!);

        // Act
        specification.Update(new TeslaVehicleConfigDto { CarType = "other", Timestamp = FixtureTimestamp - 1000 });

        // Assert
        Assert.Equal("model3", specification.CarType);
    }

    [Fact]
    public void WhenASunRoofIsInstalled_ThenHasSunRoofIsTrue()
    {
        // Arrange
        var specification = new TeslaVehicleSpecification();

        // Act
        specification.Update(new TeslaVehicleConfigDto { SunRoofInstalled = 1, Timestamp = FixtureTimestamp });

        // Assert
        Assert.True(specification.HasSunRoof);
    }
}
