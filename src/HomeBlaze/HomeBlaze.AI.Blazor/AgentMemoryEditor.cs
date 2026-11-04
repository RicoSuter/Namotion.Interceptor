using HomeBlaze.AI.Agents;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// The memory table's editor state of one agent, owned by the agent page so an unsaved edit survives tab switches.
/// Only used on the Blazor renderer's synchronization context.
/// </summary>
public sealed class AgentMemoryEditor
{
    /// <summary>The error of a save or delete while the memory is not loaded.</summary>
    public const string NotLoadedText = "The memory can be edited once the agent has started.";

    private readonly Func<AgentMemory?> _getMemory;

    // The entry as it was when editing started, to detect a concurrent write by the agent.
    private AgentMemoryEntry? _editedEntry;

    /// <param name="getMemory">
    /// Returns the agent's current memory, which is replaced when the agent starts, or null while the agent has not
    /// loaded it yet.
    /// </param>
    public AgentMemoryEditor(Func<AgentMemory?> getMemory)
    {
        _getMemory = getMemory;
    }

    /// <summary>The agent's current memory, empty while it is not loaded.</summary>
    public AgentMemorySnapshot Snapshot => _getMemory()?.GetSnapshot() ?? AgentMemorySnapshot.Empty;

    /// <summary>Whether the memory is loaded and can be edited.</summary>
    public bool IsAvailable => _getMemory() is not null;

    /// <summary>Whether the add or edit form is open.</summary>
    public bool IsEditing { get; private set; }

    /// <summary>The key of the entry being edited, null while adding.</summary>
    public string? OriginalKey => _editedEntry?.Key;

    /// <summary>The key in the form.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The value in the form.</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Whether a save or delete is being written.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Why the last save or delete failed; null after a success or when the form opens.</summary>
    public string? Error { get; private set; }

    /// <summary>Why the value in the form cannot be saved as it is, null when it fits.</summary>
    public string? ValueError => Value.Trim().Length > AgentMemory.MaxValueLength
        ? $"The value has {Value.Trim().Length} characters; the limit is {AgentMemory.MaxValueLength}. Split it across several keys."
        : null;

    /// <summary>Whether the agent changed or removed the edited entry after editing started.</summary>
    public bool IsStale => _editedEntry is { } edited && !Equals(Snapshot.TryGetEntry(edited.Key), edited);

    /// <summary>Whether saving would overwrite another existing entry with the key in the form.</summary>
    public bool ReplacesExisting =>
        IsEditing &&
        AgentMemory.ValidateKey(Key, out var key) is null &&
        !string.Equals(key, OriginalKey, StringComparison.OrdinalIgnoreCase) &&
        Snapshot.TryGetEntry(key) is not null;

    /// <summary>Opens an empty form for a new entry.</summary>
    public void StartAdd()
    {
        _editedEntry = null;
        Key = string.Empty;
        Value = string.Empty;
        Open();
    }

    /// <summary>Opens the form with an existing entry.</summary>
    public void StartEdit(AgentMemoryEntry entry)
    {
        _editedEntry = entry;
        Key = entry.Key;
        Value = entry.Value;
        Open();
    }

    /// <summary>Restarts the edit from the agent's current version of the entry, or as a new entry when it was removed.</summary>
    public void Reload()
    {
        if (_editedEntry is { } edited && Snapshot.TryGetEntry(edited.Key) is { } current)
        {
            StartEdit(current);
        }
        else
        {
            _editedEntry = null;
            Error = null;
        }
    }

    /// <summary>Closes the form without saving.</summary>
    public void Cancel()
    {
        IsEditing = false;
        _editedEntry = null;
        Error = null;
    }

    /// <summary>Writes the form; a changed key also removes the original entry in the same update.</summary>
    public async Task SaveAsync()
    {
        if ((AgentMemory.ValidateKey(Key, out var key) ?? ValueError) is { } error)
        {
            Error = error;
            return;
        }

        List<AgentMemoryChange> changes = [];
        if (OriginalKey is { } originalKey && !string.Equals(key, originalKey, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add(AgentMemoryChange.Remove(originalKey));
        }

        changes.Add(AgentMemoryChange.Set(key, Value));
        if (await TryUpdateAsync(changes))
        {
            Cancel();
        }
    }

    /// <summary>Removes the entry, closing the form when it shows that entry.</summary>
    public async Task DeleteAsync(AgentMemoryEntry entry)
    {
        if (await TryUpdateAsync([AgentMemoryChange.Remove(entry.Key)]) &&
            string.Equals(OriginalKey, entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            Cancel();
        }
    }

    private void Open()
    {
        IsEditing = true;
        Error = null;
    }

    private async Task<bool> TryUpdateAsync(IReadOnlyList<AgentMemoryChange> changes)
    {
        if (_getMemory() is not { } memory)
        {
            Error = NotLoadedText;
            return false;
        }

        IsSaving = true;
        try
        {
            var result = await memory.UpdateAsync(changes, CancellationToken.None);
            Error = result.Error;
            return result.Succeeded;
        }
        catch (Exception exception)
        {
            Error = $"The memory could not be saved: {exception.Message}";
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }
}
