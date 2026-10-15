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

    // Guards _pendingSaves and _isDisposed: writes come from the circuit, the timers fire on the thread pool.
    private readonly Lock _lock = new();
    private readonly Dictionary<IInterceptorSubject, ITimer> _pendingSaves = new(ReferenceEqualityComparer.Instance);
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
        return property is not null && property.HasPublicSetter && property.GetValue() is not MethodMetadata;
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

        if (!IsWritable(property))
        {
            ShowWriteError(property, "The property cannot be written.");
            return false;
        }

        try
        {
            var convertedValue = EditedPropertyValues.ConvertTo(value, property.Type);
            if (Equals(convertedValue, property.GetValue()))
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
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            if (_pendingSaves.TryGetValue(owner, out var timer))
            {
                timer.Change(PersistDelay, Timeout.InfiniteTimeSpan);
                return;
            }

            _pendingSaves.Add(owner, _timeProvider.CreateTimer(_onPersistDue, owner, PersistDelay, Timeout.InfiniteTimeSpan));
        }
    }

    private void OnPersistDue(object? state)
    {
        var owner = (IInterceptorSubject)state!;
        ITimer? timer;
        lock (_lock)
        {
            // Absent when Dispose has taken the save over.
            if (!_pendingSaves.Remove(owner, out timer))
            {
                return;
            }
        }

        timer.Dispose();
        _ = PersistAsync(owner, isShown: true);
    }

    private async Task PersistAsync(IInterceptorSubject owner, bool isShown)
    {
        // Runs on the thread pool and is not awaited, so every failure must end here. Saves of the same file
        // can overlap (another owner, another circuit) and fail with an IOException, which is shown as an error.
        try
        {
            var result = await ConfigurationPersistence.PersistOwnerAsync(_rootManager, owner, CancellationToken.None);
            if (isShown && result != ConfigurationPersistResult.Written)
            {
                _snackbar.AddConfigurationNotSavedWarning();
            }
        }
        catch (Exception exception)
        {
            if (isShown)
            {
                // ISnackbar is safe to call from any thread: its provider re-renders through InvokeAsync.
                _snackbar.Add($"Error saving configuration: {exception.Message}", Severity.Error);
            }
        }
    }

    private void ShowWriteError(RegisteredSubjectProperty property, string message)
    {
        _snackbar.Add($"Could not set '{property.GetDisplayName()}': {message}", Severity.Error);
    }

    /// <summary>
    /// Persists the owners that wait for their delay at once, in the background and without snackbar, and stops
    /// all timers.
    /// </summary>
    public void Dispose()
    {
        KeyValuePair<IInterceptorSubject, ITimer>[] pendingSaves;
        lock (_lock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            pendingSaves = [.. _pendingSaves];
            _pendingSaves.Clear();
        }

        foreach (var (owner, timer) in pendingSaves)
        {
            timer.Dispose();

            // Off the disposing thread, which tears the circuit down; the circuit cannot show a snackbar anymore.
            _ = Task.Run(() => PersistAsync(owner, isShown: false));
        }
    }
}
