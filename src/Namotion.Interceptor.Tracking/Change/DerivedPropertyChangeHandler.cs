using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Handles derived properties and triggers change events and recalculations when dependent properties are changed.
/// Requires LifecycleInterceptor to be added after this interceptor.
/// Thread-safe lock-free implementation with allocation-free steady state.
/// </summary>
public class DerivedPropertyChangeHandler : IReadInterceptor, IWriteInterceptor, IPropertyLifecycleHandler
{
    [ThreadStatic]
    private static DerivedPropertyRecordingBuffer? _recordingBuffer;

    public void AttachProperty(SubjectPropertyLifecycleChange change)
    {
        if (change.Property.Metadata.IsDerived)
        {
            // Record dependencies during initial evaluation
            StartRecording();
            var result = change.Property.Metadata.GetValue?.Invoke(change.Subject);
            change.Property.SetLastKnownValue(result);
            UpdateDependencyLinks(change.Property);
        }
    }

    public void DetachProperty(SubjectPropertyLifecycleChange change)
    {
        // No cleanup needed - dependencies are managed per-property
    }

    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
    {
        var result = next(ref context);

        if (_recordingBuffer?.IsRecording == true)
        {
            var property = context.Property;
            _recordingBuffer.TouchProperty(ref property);
        }

        return result;
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        next(ref context);

        var usedByProperties = context.Property.GetUsedByProperties();
        if (usedByProperties.Count == 0)
        {
            return; // No derived properties depend on this property
        }

        // Get timestamp from lifecycle interceptor (or use current)
        var timestamp = context.Property.TryGetWriteTimestamp()
            ?? SubjectChangeContext.Current.ChangedTimestamp;

        // Iterate over stable snapshot of dependents (thread-safe, lock-free)
        var snapshot = usedByProperties.AsSpan();
        for (int i = 0; i < snapshot.Length; i++)
        {
            var dependent = snapshot[i];
            if (dependent == context.Property)
            {
                continue; // Skip self-references
            }

            RecalculateDerivedProperty(ref dependent, timestamp);
        }
    }

    /// <summary>
    /// Recalculates a derived property and updates its dependencies.
    /// </summary>
    private static void RecalculateDerivedProperty(ref PropertyReference derivedProperty, DateTimeOffset timestamp)
    {
        // TODO(perf): Avoid boxing when possible (use TProperty generic parameter?)
        var oldValue = derivedProperty.GetLastKnownValue();

        // Record dependencies during recalculation
        StartRecording();
        var newValue = derivedProperty.Metadata.GetValue?.Invoke(derivedProperty.Subject);
        UpdateDependencyLinks(derivedProperty);

        derivedProperty.SetLastKnownValue(newValue);
        derivedProperty.SetWriteTimestamp(timestamp);

        // Trigger change event (derived changes have null source)
        using (SubjectChangeContext.WithSource(null))
        {
            derivedProperty.SetPropertyValueWithInterception(newValue, _ => oldValue, delegate { });
        }
    }

    /// <summary>
    /// Starts recording property accesses for dependency tracking.
    /// </summary>
    private static void StartRecording()
    {
        _recordingBuffer ??= new DerivedPropertyRecordingBuffer();
        _recordingBuffer.StartRecording();
    }

    /// <summary>
    /// Updates dependency links based on recorded property accesses.
    /// </summary>
    private static void UpdateDependencyLinks(PropertyReference derivedProperty)
    {
        var recordedDependencies = _recordingBuffer!.FinishRecording();
        var previousDependencies = derivedProperty.GetRequiredProperties();

        // Early exit if dependencies haven't changed (allocation-free)
        if (previousDependencies.SequenceEqual(recordedDependencies))
        {
            return;
        }

        // Remove derived property from old dependencies that are no longer used
        var previousSpan = previousDependencies.AsSpan();
        foreach (ref readonly var oldDependency in previousSpan)
        {
            if (!recordedDependencies.Contains(oldDependency))
            {
                oldDependency.GetUsedByProperties().Remove(derivedProperty);
            }
        }

        // Update stored dependencies
        derivedProperty.SetRequiredProperties(recordedDependencies);

        // Add derived property to new dependencies
        foreach (ref readonly var newDependency in recordedDependencies)
        {
            if (!previousSpan.Contains(newDependency))
            {
                newDependency.GetUsedByProperties().Add(derivedProperty);
            }
        }
    }
}
