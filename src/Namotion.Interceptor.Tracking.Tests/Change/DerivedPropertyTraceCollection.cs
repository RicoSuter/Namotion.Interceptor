namespace Namotion.Interceptor.Tracking.Tests.Change;

/// <summary>
/// Tests that assert on the derived property traces read the process-wide <see cref="System.Diagnostics.Trace.Listeners"/>,
/// so they run serialized and never see a trace written by another test of this collection.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DerivedPropertyTraceCollection
{
    private DerivedPropertyTraceCollection()
    {
    }

    public const string Name = "DerivedPropertyTrace";
}
