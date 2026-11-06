namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// Registers the pending state of one expression field in <see cref="PendingRows"/> and removes it when the field goes
/// away, so that a removed rule, trigger or action cannot leave a stale block on saving.
/// </summary>
public sealed class PendingRowTracker(PendingRows? registry, string fieldId) : IDisposable
{
    public void Report(bool hasPendingRow) => registry?.Set(fieldId, hasPendingRow);

    public void Dispose() => registry?.Set(fieldId, false);
}
