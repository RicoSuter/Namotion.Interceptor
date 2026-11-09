namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// Registers the pending state of one expression field in <see cref="PendingRows"/> and removes it when the field goes
/// away, so that a removed rule, trigger or action cannot leave a stale block on saving.
/// </summary>
internal sealed class PendingRowTracker(PendingRows? registry, string fieldId) : IDisposable
{
    public void Report(bool hasPendingRow) => registry?.Set(fieldId, hasPendingRow);

    /// <summary>Reports that the field dropped an edit that was not applied.</summary>
    public void Discard() => registry?.Discard("An unfinished condition");

    /// <summary>Removes the field and reports its edit as discarded when it had one.</summary>
    public void Dispose()
    {
        if (registry?.Set(fieldId, false) == true)
        {
            Discard();
        }
    }
}
