using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Runs once per write after the whole interceptor chain: requests the recalculation of every derived
/// property that depends on the written property.
/// </summary>
internal sealed class TrackingWriteObserver : IPropertyWriteObserver
{
    internal static readonly TrackingWriteObserver Instance = new();

    // The data of the derived property whose recalculation this thread publishes, so its publication finds the data
    // without a lookup.
    [ThreadStatic]
    private static DerivedPropertyData? _publication;

    internal static void EnsureRegistered() => PropertyAccessObservation.SetWriteObserver(Instance);

    /// <summary>
    /// Marks <paramref name="data"/> as published on this thread until <see cref="ExitPublication"/>, which takes the
    /// returned value.
    /// </summary>
    internal static DerivedPropertyData? EnterPublication(DerivedPropertyData data)
    {
        var previous = _publication;
        _publication = data;
        return previous;
    }

    internal static void ExitPublication(DerivedPropertyData? previous) => _publication = previous;

    public void OnPropertyWritten(in PropertyWriteObservation write)
    {
        if (!write.IsWritten)
        {
            return;
        }

        // W2: the value landed before the gate and the data are read, so an evaluation that creates the data
        // later reads this value.
        Interlocked.MemoryBarrier();
        var property = write.Property;

        // The gate is raised before the first data of the subject's type is stored: not raised means no data.
        if (!DerivedPropertyTypeGate.Of(property.Subject).IsRaised)
        {
            return;
        }

        var data = write.IsDerivedRecalculation && _publication is { } publication && publication.Property == property
            ? publication
            : property.TryGetDerivedPropertyData();
        if (data is not null)
        {
            RequestRecalculations(write, property, data);
        }
    }

    private static void RequestRecalculations(in PropertyWriteObservation write, PropertyReference property, DerivedPropertyData data)
    {
        // W4, then W5: the used-by set is read after the version moved.
        var newVersion = Interlocked.Increment(ref data.Version);
        if (DerivedPropertyRecorder.IsEvaluatingOnThisThread)
        {
            // A getter that writes what it read (or a write it triggers on this thread) does not fail the version check.
            DerivedPropertyRecorder.Current.OnOwnWrite(property, newVersion);
        }

        var rawTimestamp = write.RawTimestamp;
        try
        {
            if (!write.IsDerivedRecalculation &&
                Volatile.Read(ref data.IsDerived) &&
                property.Metadata.SetValue is not null)
            {
                DerivedPropertyOwnership.RequestRecalculation(property, ResolveTimestamp(ref rawTimestamp));
            }

            foreach (var dependent in data.GetUsedByProperties())
            {
                if (dependent != property)
                {
                    DerivedPropertyOwnership.RequestRecalculation(dependent, ResolveTimestamp(ref rawTimestamp));
                }
            }
        }
        catch (Exception) when (write.HasFailed)
        {
            // The write's own exception is in flight and wins; observers that also threw are dropped.
        }
    }

    // A write without a context has no timestamp: resolved at its first request, so every dependent shares it.
    private static long ResolveTimestamp(ref long rawTimestamp)
    {
        if (rawTimestamp == 0)
        {
            rawTimestamp = SubjectChangeContext.Current.ResolveChangedTimestamp();
        }

        return rawTimestamp;
    }
}
