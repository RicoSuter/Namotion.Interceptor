using HomeBlaze.AI.Agents;

namespace HomeBlaze.AI.Blazor;

/// <summary>
/// The memory editor's draft of one agent, owned by the agent page so unsaved edits survive tab switches.
/// Only used on the Blazor renderer's synchronization context.
/// </summary>
public sealed class AgentMemoryDraft
{
    private readonly LlmAgentBase _agent;
    private AgentMemorySnapshot _baseline = new(string.Empty, null);

    public AgentMemoryDraft(LlmAgentBase agent)
    {
        _agent = agent;
        Refresh();
    }

    /// <summary>The editor text.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>When the memory the draft started from was last written.</summary>
    public DateTimeOffset? LastUpdated => _baseline.LastUpdated;

    /// <summary>Whether the text differs from the memory it started from.</summary>
    public bool IsModified => Text != _baseline.Content;

    /// <summary>Whether the agent changed its memory after the draft was modified.</summary>
    public bool IsStale { get; private set; }

    /// <summary>Whether <see cref="SaveAsync"/> is writing the draft.</summary>
    public bool IsSaving { get; private set; }

    /// <summary>Why the last save failed; null after a successful save or a reload.</summary>
    public string? Error { get; private set; }

    /// <summary>Follows the agent's memory while the draft is unmodified, otherwise flags a newer agent write.</summary>
    public void Refresh()
    {
        var snapshot = _agent.Memory.GetSnapshot();
        if (IsModified)
        {
            IsStale = snapshot.Content != _baseline.Content;
        }
        else
        {
            Reload(snapshot);
        }
    }

    /// <summary>Discards the draft and starts from the agent's current memory.</summary>
    public void Reload() => Reload(_agent.Memory.GetSnapshot());

    /// <summary>Writes the draft to the agent's memory.</summary>
    public async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            if (await _agent.Memory.TryWriteAsync(Text, CancellationToken.None))
            {
                Reload();
            }
            else
            {
                Error = $"The memory is limited to {AgentMemory.MaxBytes} bytes.";
            }
        }
        catch (Exception exception)
        {
            Error = $"The memory could not be saved: {exception.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Reload(AgentMemorySnapshot snapshot)
    {
        _baseline = snapshot;
        Text = snapshot.Content;
        IsStale = false;
        Error = null;
    }
}
