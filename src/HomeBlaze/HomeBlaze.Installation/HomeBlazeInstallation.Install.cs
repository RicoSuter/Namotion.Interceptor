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
    private readonly Lock _installLock = new();
    private readonly SemaphoreSlim _installSignal = new(0, 1);

    // Monotonic time of the last request, for the pick-up timeout; written and read under _installLock.
    private long _requestTimestamp;

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

    private void SetInstallState(UpdateInstallState state, string? message)
    {
        UpdateInstallState = state;
        UpdateInstallStatus = message;
    }

    /// <summary>
    /// Moves an active install one step: picked up, finished or timed out. Only the monitor and the tests call it.
    /// </summary>
    internal void AdvanceInstall()
    {
        lock (_installLock)
        {
            try
            {
                var files = GetUpdaterFiles();
                if (files is null)
                {
                    return;
                }

                if (UpdateInstallState == UpdateInstallState.Requested)
                {
                    AdvanceRequested(files);
                }

                if (UpdateInstallState == UpdateInstallState.Installing)
                {
                    EvaluateRun(files);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Reading the update files failed");
                CompleteInstall(UpdateInstallState.Failed, exception.Message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Advancing the update install failed");
                CompleteInstall(UpdateInstallState.Failed, exception.Message);
            }
        }
    }

    /// <summary>
    /// Rebuilds the install state from the files a previous process left, before the first poll: a run without a
    /// result means the updater is still finishing the recreate that started this process.
    /// </summary>
    internal void RestoreInstall()
    {
        lock (_installLock)
        {
            try
            {
                var files = GetUpdaterFiles();
                if (files is null)
                {
                    return;
                }

                // The process that wrote a request the updater has not taken is gone.
                files.TryDeleteRequest();

                if (files.ReadRun() is null)
                {
                    files.DeleteRunAndResult();
                    if (UpdateInstallState == UpdateInstallState.Requested)
                    {
                        SetInstallState(UpdateInstallState.None, null);
                    }

                    return;
                }

                SetInstallState(UpdateInstallState.Installing, "Installing update…");
                EvaluateRun(files);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Reading the update files failed");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // ExecuteAsync calls this before the loops start, so an escaping exception would stop the installation.
                _logger.LogWarning(exception, "Restoring the update install failed");
            }
        }
    }

    private void AdvanceRequested(UpdaterFiles files)
    {
        // Reading the run before deleting keeps a request the updater took at the last moment from counting as lost.
        if (files.ReadRun() is not null)
        {
            SetInstallState(UpdateInstallState.Installing, "Installing update…");
            return;
        }

        if (_timeProvider.GetElapsedTime(_requestTimestamp) < PickUpTimeout)
        {
            return;
        }

        files.TryDeleteRequest();
        if (files.ReadRun() is not null)
        {
            SetInstallState(UpdateInstallState.Installing, "Installing update…");
            return;
        }

        CompleteInstall(UpdateInstallState.Failed, "The updater did not pick up the request.");
    }

    private void EvaluateRun(UpdaterFiles files)
    {
        var run = files.ReadRun();
        if (run is null)
        {
            CompleteInstall(UpdateInstallState.Failed, "The update files were removed.");
            return;
        }

        if (files.TryReadExitCode(out var exitCode))
        {
            CompleteRun(files, run, exitCode);
            return;
        }

        if (_timeProvider.GetUtcNow() - run.RequestedAt >= InstallTimeout)
        {
            files.DeleteRunAndResult();
            CompleteInstall(UpdateInstallState.Failed, "The update did not finish within 30 minutes.");
        }
    }

    private void CompleteRun(UpdaterFiles files, UpdaterRun run, int exitCode)
    {
        var log = files.ReadLog();
        files.DeleteRunAndResult();
        var running = new BuildIdentity(Version, Commit);
        if (exitCode != 0)
        {
            _logger.LogWarning("Installing the update failed with exit code {ExitCode}: {Log}", exitCode, log);
            CompleteInstall(UpdateInstallState.Failed, GetLastLine(log) ?? $"The update failed with exit code {exitCode}.");
        }
        else if (run.Requested != running)
        {
            CompleteInstall(UpdateInstallState.Succeeded, $"Updated from {run.Requested.Describe()} to {running.Describe()}");
        }
        else
        {
            CompleteInstall(UpdateInstallState.UpToDate, "No newer image was found for the configured tag.");
        }
    }

    private void CompleteInstall(UpdateInstallState state, string message)
    {
        SetInstallState(state, message);
        LastUpdateInstall = _timeProvider.GetUtcNow();
        _logger.LogInformation("Update install {State}: {Message}", state, message);
    }

    private static string? GetLastLine(string? log) =>
        log?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

    private async Task RunInstallMonitorAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsInstallingUpdate)
            {
                await WaitAsync(_installSignal, Timeout.InfiniteTimeSpan, stoppingToken);
                continue;
            }

            try
            {
                AdvanceInstall();
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // The host never observes ExecuteTask, so an escaping exception would silently end the monitor.
                _logger.LogError(exception, "Monitoring the update install failed unexpectedly");
                lock (_installLock)
                {
                    CompleteInstall(UpdateInstallState.Failed, exception.Message);
                }
            }

            await WaitAsync(_installSignal, InstallMonitorInterval, stoppingToken);
        }
    }
}
