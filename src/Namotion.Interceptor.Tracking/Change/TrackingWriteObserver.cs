using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Runs once per write after the whole interceptor chain: requests the recalculation of every derived
/// property that depends on the written property.
/// </summary>
internal sealed class TrackingWriteObserver : IPropertyWriteObserver
{
    internal static readonly TrackingWriteObserver Instance = new();

    internal static void EnsureRegistered() => PropertyAccessObservation.SetWriteObserver(Instance);

    public void OnPropertyWritten(in PropertyWriteObservation write)
    {
        if (!write.IsWritten)
        {
            return;
        }

        // W2: the value landed before the data lookup, so an evaluation that creates the data later
        // reads this value.
        Interlocked.MemoryBarrier();
        var property = write.Property;
        var data = property.TryGetDerivedPropertyData();
        if (data is null)
        {
            return;
        }

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
