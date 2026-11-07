namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// Which expression fields of the editor have a row with an unapplied invalid edit. An unfinished path shows no error on
/// its row, so the editor blocks the save while any field is listed here and says why. The editor cascades one instance
/// to its tabs; a <see cref="PendingRowTracker"/> registers a field and removes it again when it goes away.
/// </summary>
internal sealed class PendingRows
{
    private readonly HashSet<string> _fieldIds = new(StringComparer.Ordinal);

    /// <summary>Raised after a field was marked or unmarked, on the thread that changed it, which may be one that disposes a component.</summary>
    public event Action? Changed;

    public bool Any => _fieldIds.Count > 0;

    /// <summary>Marks or unmarks a field, identified by an id that is unique in the editor.</summary>
    /// <returns>True when the state of the field changed.</returns>
    public bool Set(string fieldId, bool hasPendingRow)
    {
        var isChanged = hasPendingRow ? _fieldIds.Add(fieldId) : _fieldIds.Remove(fieldId);
        if (isChanged)
        {
            Changed?.Invoke();
        }

        return isChanged;
    }
}
