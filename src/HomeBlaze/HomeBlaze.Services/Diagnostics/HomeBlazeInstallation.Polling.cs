using System.Collections.Immutable;
using HomeBlaze.Abstractions;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;

namespace HomeBlaze.Services.Diagnostics;

public partial class HomeBlazeInstallation
{
    private static readonly TimeSpan MinimumPollingInterval = TimeSpan.FromSeconds(1);

    // SemaphoreSlim.WaitAsync(TimeSpan) throws above int.MaxValue milliseconds (about 24.8 days).
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromMilliseconds(int.MaxValue);

    private readonly SemaphoreSlim _pollSignal = new(0, 1);

    // Set by AssemblyLoad on any thread and cleared by the poll that rebuilds the inventory.
    private int _isInventoryOutdated = 1;

    // Only the polling loop reads and writes it.
    private bool _hasPollFailed;

    /// <summary>
    /// Refreshes the environment and, after an assembly was loaded, the inventory. Only the polling loop calls it.
    /// </summary>
    internal void Poll()
    {
        var context = ((IInterceptorSubject)this).Context;

        var dataDirectory = context.TryGetService<IDataDirectoryProvider>()?.DataDirectory;
        DataDirectory = dataDirectory;
        (DataVolumeFreeSpace, DataVolumeTotalSize) = ReadVolume(dataDirectory);

        if (Interlocked.Exchange(ref _isInventoryOutdated, 0) == 1)
        {
            try
            {
                ApplyInventory(AssemblyInventory.Create(LoadedAssemblyReader.Read()));
            }
            catch
            {
                Volatile.Write(ref _isInventoryOutdated, 1);
                throw;
            }
        }

        LastUpdated = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Applies an inventory. Repositories that stay under the same name and URL keep their subject, so their paths
    /// and history stay.
    /// </summary>
    internal void ApplyInventory(ImmutableArray<InventoryRepository> inventory)
    {
        var current = Repositories;
        var repositories = new Dictionary<string, InstallationRepository>(inventory.Length, StringComparer.Ordinal);
        var isChanged = inventory.Length != current.Count;
        foreach (var entry in inventory)
        {
            if (!current.TryGetValue(entry.Name, out var repository) || !repository.HasRepositoryUrl(entry.RepositoryUrl))
            {
                repository = new InstallationRepository(entry.Name, entry.RepositoryUrl);
                isChanged = true;
            }

            repository.Apply(entry);
            repositories[entry.Name] = repository;
        }

        if (isChanged)
        {
            Repositories = repositories;
        }
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

            var delay = PollingInterval < MinimumPollingInterval ? MinimumPollingInterval : PollingInterval;
            await WaitAsync(_pollSignal, delay, stoppingToken);
        }
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

    /// <summary>
    /// Waits for the signal or the delay and returns whether the signal woke it; false also when stopping.
    /// </summary>
    private static async Task<bool> WaitAsync(SemaphoreSlim signal, TimeSpan delay, CancellationToken stoppingToken)
    {
        if (delay != Timeout.InfiniteTimeSpan && delay > MaximumDelay)
        {
            delay = MaximumDelay;
        }

        try
        {
            return await signal.WaitAsync(delay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        // The inventory leaves dynamic assemblies out, so emitting one does not change it.
        if (!args.LoadedAssembly.IsDynamic)
        {
            Interlocked.Exchange(ref _isInventoryOutdated, 1);
        }
    }

    private void OnDeveloperModeChanged()
    {
        IsDeveloperMode = _developerModeService.IsEnabled;
    }
}
