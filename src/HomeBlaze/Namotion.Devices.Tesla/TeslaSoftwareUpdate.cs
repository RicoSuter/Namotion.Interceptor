using CarServer;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Devices.Tesla.Client.Dto;
using Namotion.Devices.Tesla.Mapping;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Tesla;

/// <summary>
/// The software update of a Tesla vehicle. <see cref="Status"/> and <see cref="Version"/> are null while no update is pending.
/// </summary>
[ExcludeFromCreation]
[InterceptorSubject]
public partial class TeslaSoftwareUpdate : ITitleProvider, IIconProvider
{
    private readonly ILogger _logger;
    private readonly TeslaSectionClock _clock = new();

    public string Title => "Software Update";

    public string IconName => "SystemUpdate";

    [State(IsDiscrete = true, Position = 1)] public partial TeslaSoftwareUpdateStatus? Status { get; internal set; }

    /// <summary>The version of the pending update.</summary>
    [State(Position = 2)] public partial string? Version { get; internal set; }

    [State(Unit = StateUnit.Percent, Position = 3)] public partial decimal? DownloadProgress { get; internal set; }
    [State(Unit = StateUnit.Percent, Position = 4)] public partial decimal? InstallProgress { get; internal set; }

    /// <summary>The time the installation is expected to take.</summary>
    [State(Position = 5)] public partial TimeSpan? ExpectedDuration { get; internal set; }

    internal TeslaSoftwareUpdate(ILogger logger)
    {
        _logger = logger;
        Status = null;
        Version = null;
        DownloadProgress = null;
        InstallProgress = null;
        ExpectedDuration = null;
    }

    /// <param name="dto">The software update of the JSON vehicle state.</param>
    /// <param name="timestamp">The timestamp of that vehicle state, because the software update has none.</param>
    internal void Update(TeslaSoftwareUpdateDto dto, DateTimeOffset? timestamp)
    {
        if (!_clock.TryAdvance(timestamp))
        {
            return;
        }

        Status = TeslaValues.Enum<TeslaSoftwareUpdateStatus>(dto.Status, _logger);
        Version = TeslaValues.Text(dto.Version);
        DownloadProgress = TeslaValues.Percent(dto.DownloadPerc);
        InstallProgress = TeslaValues.Percent(dto.InstallPerc);
        ExpectedDuration = TeslaValues.FromSeconds(dto.ExpectedDurationSec);
    }

    // The snapshot also has the scheduled time and the remaining warning time, which the JSON of this library does not read.
    internal void Update(SoftwareUpdateState proto)
    {
        if (!_clock.TryAdvance(proto.Timestamp?.ToDateTimeOffset()))
        {
            return;
        }

        UpdateStatusFromProtobuf(proto);
        if (proto.OptionalVersionCase != SoftwareUpdateState.OptionalVersionOneofCase.None) Version = TeslaValues.Text(proto.Version);
        if (proto.OptionalDownloadPercCase != SoftwareUpdateState.OptionalDownloadPercOneofCase.None) DownloadProgress = TeslaValues.Percent(proto.DownloadPerc);
        if (proto.OptionalInstallPercCase != SoftwareUpdateState.OptionalInstallPercOneofCase.None) InstallProgress = TeslaValues.Percent(proto.InstallPerc);
        if (proto.OptionalExpectedDurationSecCase != SoftwareUpdateState.OptionalExpectedDurationSecOneofCase.None) ExpectedDuration = TeslaValues.FromSeconds(proto.ExpectedDurationSec);
    }

    private void UpdateStatusFromProtobuf(SoftwareUpdateState proto)
    {
        if (proto.Status is not { } status)
        {
            return;
        }

        // An explicit Unknown is how the snapshot reports no pending update, so it clears the status instead of keeping it.
        if (status.TypeCase == SoftwareUpdateState.Types.SoftwareUpdateStatus.TypeOneofCase.Unknown)
        {
            Status = null;
        }
        else if (TeslaProtoValues.SoftwareUpdateStatus(status) is { } mappedStatus)
        {
            Status = mappedStatus;
        }
    }
}
