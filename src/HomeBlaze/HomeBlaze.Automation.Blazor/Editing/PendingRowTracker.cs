namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>Lists one expression field in <see cref="UnappliedInputs"/> while it has a row that is not finished, until the field goes away.</summary>
internal sealed class PendingRowTracker(UnappliedInputs? inputs, string fieldId) : IDisposable
{
    public void Report(bool hasPendingRow) => inputs?.SetPendingRow(fieldId, hasPendingRow);

    /// <summary>Reports that the field dropped an edit that was not applied.</summary>
    public void Discard() => inputs?.Discard("An unfinished condition");

    /// <summary>Removes the field and reports its edit as discarded when it had one.</summary>
    public void Dispose()
    {
        if (inputs?.SetPendingRow(fieldId, false) == true)
        {
            Discard();
        }
    }
}
