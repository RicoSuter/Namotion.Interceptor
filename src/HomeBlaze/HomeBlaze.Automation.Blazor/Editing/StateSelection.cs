namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// The state that the States tab edits: its name as typed and the error of a rename that was not applied. The error is
/// also an input error of the draft, so that a rename that was not applied cannot be lost by saving.
/// </summary>
internal sealed class StateSelection(AutomationDraft draft) : IDisposable
{
    private readonly string _inputId = Guid.NewGuid().ToString("N");

    public string? SelectedState { get; private set; }

    /// <summary>The name in the field, which keeps a rejected name as typed.</summary>
    public string? Name { get; private set; }

    public string? RenameError { get; private set; }

    /// <summary>The name the selected state had before its last rename, or null.</summary>
    public string? RenamedFrom { get; private set; }

    /// <summary>Selects a state, or none, and returns whether this cleared a rename error.</summary>
    public bool Select(string? state)
    {
        SelectedState = state;
        Name = state;
        RenamedFrom = null;
        return SetRenameError(null);
    }

    /// <summary>Renames the selected state and returns whether the draft changed. A name that differs only in white space at its ends is no rename.</summary>
    public bool Rename(string? newName)
    {
        Name = newName;
        if (SelectedState is not { } state || newName?.Trim() == state)
        {
            SetRenameError(null);
            return false;
        }

        if (!draft.RenameState(state, newName ?? string.Empty))
        {
            SetRenameError(string.IsNullOrWhiteSpace(newName) ? "Enter a name" : $"'{newName.Trim()}' is already a state");
            return false;
        }

        Select(newName!.Trim());
        RenamedFrom = state;
        return true;
    }

    /// <summary>Deletes a state that no rule uses and returns whether the draft changed.</summary>
    public bool Remove(string state)
    {
        if (!draft.RemoveState(state))
        {
            return false;
        }

        Select(null);
        return true;
    }

    public void Dispose() => draft.SetInputError(_inputId, null);

    private bool SetRenameError(string? error)
    {
        var isChanged = RenameError != error;
        RenameError = error;
        draft.SetInputError(_inputId, error is null ? null : new AutomationValidationError(null, $"State name: {error}"));
        return isChanged;
    }
}
