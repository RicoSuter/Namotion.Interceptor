namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// The inputs of the editor that hold text the draft does not have: an expression field with a row that is not finished,
/// which shows no error on its row, and an input whose text does not parse, such as a duration or a state name, with its
/// error. The editor blocks saving while any input is listed. It cascades one instance to its tabs; an input removes
/// itself again when it goes away, so that a removed rule, trigger or action leaves no stale block.
/// </summary>
internal sealed class UnappliedInputs
{
    private readonly HashSet<string> _pendingFieldIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AutomationValidationError> _errors = new(StringComparer.Ordinal);

    /// <summary>Raised after an input was listed or removed, on the thread that changed it, which may be one that disposes a component.</summary>
    public event Action? Changed;

    /// <summary>Raised with what was discarded when an input goes away with text that was not applied.</summary>
    public event Action<string>? Discarded;

    /// <summary>Whether an expression field has a row that is not finished.</summary>
    public bool HasPendingRow => _pendingFieldIds.Count > 0;

    /// <summary>The errors of the inputs whose text does not parse.</summary>
    public IEnumerable<AutomationValidationError> Errors => _errors.Values;

    /// <summary>Lists or removes an expression field with a row that is not finished, identified by an id that is unique in the editor.</summary>
    /// <returns>True when the state of the field changed.</returns>
    public bool SetPendingRow(string fieldId, bool hasPendingRow)
    {
        var isChanged = hasPendingRow ? _pendingFieldIds.Add(fieldId) : _pendingFieldIds.Remove(fieldId);
        if (isChanged)
        {
            Changed?.Invoke();
        }

        return isChanged;
    }

    /// <summary>Sets or clears the error of an input whose text does not parse, identified by an id that is unique in the editor.</summary>
    public void SetError(string inputId, AutomationValidationError? error)
    {
        bool isChanged;
        if (error is null)
        {
            isChanged = _errors.Remove(inputId);
        }
        else
        {
            isChanged = !_errors.TryGetValue(inputId, out var previous) || previous != error;
            _errors[inputId] = error;
        }

        if (isChanged)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Reports that an input went away with text that was not applied.</summary>
    public void Discard(string input) => Discarded?.Invoke(input);
}
