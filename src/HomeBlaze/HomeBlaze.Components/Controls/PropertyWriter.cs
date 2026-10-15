using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Editors;
using HomeBlaze.Services;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Controls;

/// <summary>
/// Writes property values for the controls of a page and persists a written [Configuration] property through its
/// configuration owner, once per owner after the writes to it settle. Registered per circuit.
/// </summary>
public sealed class PropertyWriter : IDisposable
{
    /// <summary>
    /// The time after the last write to a configuration owner until the owner is persisted.
    /// </summary>
    public static readonly TimeSpan PersistDelay = TimeSpan.FromSeconds(1);

    private readonly RootManager _rootManager;
    private readonly ISnackbar _snackbar;
    private readonly TimeProvider _timeProvider;
    private readonly TimerCallback _onPersistDue;

    // Guards the dictionaries and _isDisposed: writes come from the circuit, the timers fire on the thread pool.
    private readonly Lock _lock = new();
    private readonly Dictionary<IInterceptorSubject, ITimer> _pendingSaves = new(ReferenceEqualityComparer.Instance);

    // The last started save per owner until it completes; the next save of the owner awaits it, so saves of one
    // owner never overlap and run in the order they started.
    private readonly Dictionary<IInterceptorSubject, Task> _runningSaves = new(ReferenceEqualityComparer.Instance);
    private volatile bool _isDisposed;

    public PropertyWriter(RootManager rootManager, ISnackbar snackbar, TimeProvider timeProvider)
    {
        _rootManager = rootManager;
        _snackbar = snackbar;
        _timeProvider = timeProvider;
        _onPersistDue = OnPersistDue;
    }

    /// <summary>
    /// Gets whether a control may write the property: it exists, has a public setter and holds no operation.
    /// </summary>
    public static bool IsWritable(RegisteredSubjectProperty? property)
    {
        return property is not null && IsWritable(property, property.GetValue());
    }

    /// <summary>
    /// Gets whether a control may write the property, given its current value that the caller has already read.
    /// </summary>
    public static bool IsWritable(RegisteredSubjectProperty property, object? currentValue)
    {
        return property.HasPublicSetter && currentValue is not MethodMetadata;
    }

    /// <summary>
    /// Converts the value to the type of the property and sets it. A [Configuration] property is persisted through
    /// its configuration owner <see cref="PersistDelay"/> after the last write to that owner; a save that stores
    /// nothing shows a warning and one that fails shows an error, both leave the value applied. Nothing is rolled back.
    /// </summary>
    /// <returns>
    /// False when the property is not writable or the conversion or the setter failed, which is shown as an error.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
    public bool TryWrite(RegisteredSubjectProperty property, object? value)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var currentValue = property.GetValue();
        if (!IsWritable(property, currentValue))
        {
            ShowWriteError(property, "The property cannot be written.");
            return false;
        }

        try
        {
            var convertedValue = EditedPropertyValues.ConvertTo(value, property.Type);
            if (Equals(convertedValue, currentValue))
            {
                return true;
            }

            property.SetValue(convertedValue);
        }
        catch (Exception exception)
        {
            ShowWriteError(property, exception.Message);
            return false;
        }

        if (property.IsConfigurationProperty())
        {
            // A subject without owner is keyed by itself, so its "not saved" warning is debounced as well.
            SchedulePersist(property.Subject.TryGetConfigurationOwner() ?? property.Subject);
        }

        return true;
    }

    private void SchedulePersist(IInterceptorSubject owner)
    {
        Task? previousSave;
        TaskCompletionSource save;
        lock (_lock)
        {
            if (!_isDisposed)
            {
                if (_pendingSaves.TryGetValue(owner, out var timer))
                {
                    timer.Change(PersistDelay, Timeout.InfiniteTimeSpan);
                }
                else
                {
                    _pendingSaves.Add(owner, _timeProvider.CreateTimer(_onPersistDue, owner, PersistDelay, Timeout.InfiniteTimeSpan));
                }

                return;
            }

            // Disposed after this write passed its check: saved the way Dispose saves a pending owner.
            save = StartSave(owner, out previousSave);
        }

        SaveInBackground(owner, previousSave, save);
    }

    /// <summary>Starts the save on the thread pool, isolated so the reschedule path allocates no closure.</summary>
    private void SaveInBackground(IInterceptorSubject owner, Task? previousSave, TaskCompletionSource save) =>
        _ = Task.Run(() => PersistAsync(owner, previousSave, save, isShown: false));

    private void OnPersistDue(object? state)
    {
        var owner = (IInterceptorSubject)state!;
        ITimer? timer;
        Task? previousSave;
        TaskCompletionSource save;
        lock (_lock)
        {
            // Absent when Dispose has taken the save over.
            if (!_pendingSaves.Remove(owner, out timer))
            {
                return;
            }

            save = StartSave(owner, out previousSave);
        }

        timer.Dispose();
        _ = PersistAsync(owner, previousSave, save, isShown: true);
    }

    /// <summary>Registers a save of the owner as its running one; the caller holds <see cref="_lock"/>.</summary>
    private TaskCompletionSource StartSave(IInterceptorSubject owner, out Task? previousSave)
    {
        _runningSaves.TryGetValue(owner, out previousSave);

        // Asynchronous continuations, so the next save does not run inside the completion of this one.
        var save = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runningSaves[owner] = save.Task;
        return save;
    }

    private async Task PersistAsync(IInterceptorSubject owner, Task? previousSave, TaskCompletionSource save, bool isShown)
    {
        try
        {
            if (previousSave is not null)
            {
                // Never faults: it is completed with SetResult only.
                await previousSave;
            }

            // Runs on the thread pool and is not awaited, so a failing save must end here. Saves of the same file
            // can overlap (another owner, another circuit) and fail with an IOException, which is shown as an error.
            ConfigurationPersistResult result;
            try
            {
                result = await ConfigurationPersistence.PersistOwnerAsync(_rootManager, owner, CancellationToken.None);
            }
            catch (Exception exception)
            {
                if (isShown)
                {
                    // ISnackbar is safe to call from any thread: its provider re-renders through InvokeAsync.
                    _snackbar.Add($"Error saving configuration: {exception.Message}", Severity.Error);
                }

                return;
            }

            if (isShown && result != ConfigurationPersistResult.Written)
            {
                _snackbar.AddConfigurationNotSavedWarning();
            }
        }
        finally
        {
            lock (_lock)
            {
                if (_runningSaves.TryGetValue(owner, out var runningSave) && runningSave == save.Task)
                {
                    _runningSaves.Remove(owner);
                }
            }

            save.SetResult();
        }
    }

    private void ShowWriteError(RegisteredSubjectProperty property, string message)
    {
        _snackbar.Add($"Could not set '{property.GetDisplayName()}': {message}", Severity.Error);
    }

    /// <summary>
    /// Persists the owners that wait for their delay at once, each after its running save, in the background and
    /// without snackbar, and stops all timers. A write in progress while the writer is disposed is saved the same way.
    /// </summary>
    public void Dispose()
    {
        (IInterceptorSubject Owner, ITimer Timer, Task? PreviousSave, TaskCompletionSource Save)[] pendingSaves;
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            pendingSaves = new (IInterceptorSubject, ITimer, Task?, TaskCompletionSource)[_pendingSaves.Count];
            var index = 0;
            foreach (var (owner, timer) in _pendingSaves)
            {
                var save = StartSave(owner, out var previousSave);
                pendingSaves[index++] = (owner, timer, previousSave, save);
            }

            _pendingSaves.Clear();
        }

        foreach (var (owner, timer, previousSave, save) in pendingSaves)
        {
            timer.Dispose();

            // Off the disposing thread, which tears the circuit down; the circuit cannot show a snackbar anymore.
            _ = Task.Run(() => PersistAsync(owner, previousSave, save, isShown: false));
        }
    }
}
