using System.ComponentModel;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Installation;

public partial class HomeBlazeInstallation
{
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan PickUpTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan InstallMonitorInterval = TimeSpan.FromSeconds(2);

    // Guards the install state and the files it decides on, so InstallUpdate and the monitor never interleave.
    internal readonly Lock _installLock = new();
    internal readonly SemaphoreSlim _installSignal = new(0, 1);

    // Monotonic time of the last request, for the pick-up timeout; written and read under _installLock.
    internal long _requestTimestamp;

    /// <summary>
    /// Whether an updater sidecar runs next to this installation, judged by its heartbeat in the data directory.
    /// </summary>
    [State(IsDiscrete = true, Position = 468)]
    [Category("Update")]
    public partial bool IsUpdaterAvailable { get; internal set; }

    [State(IsDiscrete = true, Position = 469)]
    [Category("Update")]
    public partial UpdateInstallState UpdateInstallState { get; internal set; }

    /// <summary>
    /// What the current or last install is doing or did, or why it failed.
    /// </summary>
    [State(Position = 470)]
    [Category("Update")]
    public partial string? UpdateInstallStatus { get; internal set; }

    /// <summary>
    /// When the last install reached a final state; null while none did since the start.
    /// </summary>
    [State(Position = 471)]
    [Category("Update")]
    public partial DateTimeOffset? LastUpdateInstall { get; internal set; }

    [Derived]
    public bool IsInstallingUpdate =>
        UpdateInstallState is UpdateInstallState.Requested or UpdateInstallState.Installing;

    [Derived]
    [PropertyAttribute(nameof(InstallUpdate), KnownAttributes.IsEnabled)]
    public bool InstallUpdate_IsEnabled =>
        IsUpdaterAvailable && Status == ServiceStatus.Running && UpdateAvailable == true && !IsInstallingUpdate;

    /// <summary>
    /// Asks the updater sidecar to pull the image of the configured tag and recreate this installation's container.
    /// </summary>
    [Operation(Title = "Install update", Icon = "SystemUpdateAlt", RequiresConfirmation = true,
        Description = "Pulls the image of the configured tag and restarts HomeBlaze on it.")]
    public void InstallUpdate()
    {
        lock (_installLock)
        {
            var files = GetUpdaterFiles();
            if (files is null || !IsHeartbeatFresh(files))
            {
                IsUpdaterAvailable = false;
                throw new InvalidOperationException("No updater runs next to this installation.");
            }

            if (Status != ServiceStatus.Running)
            {
                throw new InvalidOperationException("The installation is not running.");
            }

            if (UpdateAvailable != true)
            {
                throw new InvalidOperationException("No update is available.");
            }

            if (IsInstallingUpdate)
            {
                throw new InvalidOperationException("An update is already being installed.");
            }

            files.WriteRequest(new BuildIdentity(Version, Commit));
            _requestTimestamp = _timeProvider.GetTimestamp();
            SetInstallState(UpdateInstallState.Requested, "Waiting for the updater…");
        }

        Release(_installSignal);
    }

    /// <summary>
    /// Sets <see cref="IsUpdaterAvailable"/> from the heartbeat; false without a data directory or on an I/O error.
    /// </summary>
    internal void RefreshUpdaterAvailability()
    {
        try
        {
            IsUpdaterAvailable = GetUpdaterFiles() is { } files && IsHeartbeatFresh(files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            IsUpdaterAvailable = false;
        }
    }

    internal UpdaterFiles? GetUpdaterFiles() =>
        ((IInterceptorSubject)this).Context.TryGetService<IDataDirectoryProvider>()?.DataDirectory is { } directory
            ? new UpdaterFiles(directory, _timeProvider)
            : null;

    private static bool IsHeartbeatFresh(UpdaterFiles files) =>
        files.GetHeartbeatAge() is { } age && age < HeartbeatTimeout;

    private void SetInstallState(UpdateInstallState state, string message)
    {
        UpdateInstallState = state;
        UpdateInstallStatus = message;
    }
}
