using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Diagnostics;

public partial class HomeBlazeInstallation
{
    private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromSeconds(1);

    // SemaphoreSlim.WaitAsync(TimeSpan) throws above int.MaxValue milliseconds (about 24.8 days).
    private static readonly TimeSpan MaximumPollingDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly SemaphoreSlim _pollSignal = new(0, 1);

    // Only the polling loop reads and writes it.
    private bool _hasPollFailed;

    /// <summary>
    /// Refreshes the environment. Only the polling loop calls it.
    /// </summary>
    internal void Poll()
    {
        var context = ((IInterceptorSubject)this).Context;

        var dataDirectory = context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        DataDirectory = dataDirectory;
        (DataVolumeFreeSpace, DataVolumeTotalSize) = ReadVolume(dataDirectory);

        LastUpdated = _timeProvider.GetUtcNow();
    }

    private async Task RunPollingAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Poll();
                _hasPollFailed = false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                // The host never observes ExecuteTask, so an escaping exception would silently end the polling.
                if (!_hasPollFailed)
                {
                    _logger.LogWarning(exception, "Reading the installation values failed");
                    _hasPollFailed = true;
                }
            }

            await WaitAsync(_pollSignal, GetPollingDelay(), stoppingToken);
        }
    }

    private TimeSpan GetPollingDelay()
    {
        if (PollingInterval < MinimumPollingInterval)
        {
            return MinimumPollingInterval;
        }

        return PollingInterval > MaximumPollingDelay ? MaximumPollingDelay : PollingInterval;
    }

    private static (long? FreeSpace, long? TotalSize) ReadVolume(string? directory)
    {
        if (directory is null)
        {
            return (null, null);
        }

        try
        {
            // On Linux and macOS a drive is any path, and its values are those of the file system that holds it.
            var drive = new DriveInfo(directory);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }
}
