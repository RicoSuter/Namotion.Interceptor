using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.AI.Agents;

public abstract partial class LlmAgentBase
{
    /// <summary>Discards the chat session once no turn runs; memory and timeline stay.</summary>
    [Operation(Title = "New Conversation", Icon = "RestartAlt", Position = 2, RequiresConfirmation = true)]
    public async Task NewConversationAsync(CancellationToken cancellationToken)
    {
        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            await ResetSessionAsync(cancellationToken);
            await AppendDividerAsync("New conversation");
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Sends a chat message and waits for the reply, which is appended to the timeline. Failures are recorded in
    /// the timeline instead of thrown. A message cancelled through <paramref name="cancellationToken"/> or by the
    /// agent stopping is recorded as an error entry with <see cref="CancelledText"/>, so background findings
    /// stay pending for the next message; only cancellation through <paramref name="cancellationToken"/> throws.
    /// Returns once the reply is recorded; <see cref="OnChatTurnCompletedAsync"/> may still run, and the next turn
    /// and a stop of the agent wait for it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The agent is not running; the message is not recorded.</exception>
    public async Task SendMessageAsync(string text, CancellationToken cancellationToken)
    {
        CancellationTokenSource? turnCancellation = null;
        var lifetimeToken = CancellationToken.None;
        lock (_stateLock)
        {
            if (_lifetime is not null)
            {
                // Linked under the lock: ExecuteAsync disposes the lifetime source only after clearing it here.
                lifetimeToken = _lifetime.Token;
                turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
            }
        }

        // Checked before the message is recorded: before the first start the timeline is a placeholder that the
        // stored timeline replaces.
        if (turnCancellation is null)
        {
            throw new InvalidOperationException(NotRunningText);
        }

        using (turnCancellation)
        {
            await AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.User,
                Timestamp = _services.TimeProvider.GetUtcNow(),
                Text = text
            });

            await RunChatTurnAsync(text, turnCancellation.Token, lifetimeToken, cancellationToken);
        }
    }

    /// <summary>
    /// Enters the turn gate and answers a chat message whose user entry is recorded; see <see cref="SendMessageAsync"/>.
    /// </summary>
    private async Task RunChatTurnAsync(string text, CancellationToken turnToken, CancellationToken lifetimeToken, CancellationToken cancellationToken)
    {
        if (!await TryEnterGateForChatAsync(turnToken))
        {
            await AppendAsync(CreateErrorEntry(CancelledText, tools: null));
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }

        AgentToolSet? tools = null;
        var isIdle = false;
        var isAnswered = false;
        try
        {
            var started = BeginTurn(AnsweringStatusMessage);
            await WriteRunStatisticsAsync();
            var progress = CreateProgress();
            var allowedTools = AllowedTools;
            tools = CreateToolSet(allowedTools, progress);
            var message = AgentPromptBuilder.BuildChatMessage(text, Timeline.GetFindingsSinceLastReply());
            var result = await RunTurnAsync(AgentTurnKind.Chat, message, allowedTools, tools, progress, turnToken);
            ClearStreamingText();
            await AppendAsync(new AgentTimelineEntry
            {
                Kind = AgentTimelineEntryKind.Assistant,
                Timestamp = _services.TimeProvider.GetUtcNow(),
                Text = result.Text,
                ToolCalls = tools.Calls,
                Duration = _services.TimeProvider.GetUtcNow() - started,
                Usage = result.Usage
            });

            // A working chat turn proves the backend works again, so it lifts pauses like a background run.
            lock (_stateLock)
            {
                _scheduler?.ReportSuccess();
            }

            isIdle = true;
            isAnswered = true;
        }
        catch (OperationCanceledException) when (turnToken.IsCancellationRequested)
        {
            await AppendAsync(CreateErrorEntry(CancelledText, tools));
            isIdle = true;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            var turnException = ToTurnException(exception);
            await HandleFailureAsync(turnException, CreateErrorEntry(turnException.Message, tools));
        }
        finally
        {
            EndTurn(isIdle);
            if (isAnswered)
            {
                // Takes over the gate, so the next turn and a stop wait for the upkeep but this sender does not.
                _ = Task.Run(() => RunChatTurnUpkeepAsync(lifetimeToken), CancellationToken.None);
            }
            else
            {
                _turnGate.Release();
            }
        }
    }

    // Runs while the caller holds the turn gate for it, and releases it.
    private async Task RunChatTurnUpkeepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await OnChatTurnCompletedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The answer is already recorded; the upkeep is left to a later turn.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The work after a chat turn of agent {Agent} failed.", Title);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Enters the turn gate for a chat turn, showing a waiting message while another turn holds it. Returns
    /// false, with the gate released and the waiting message removed again, when <paramref name="cancellationToken"/>
    /// is cancelled first.
    /// </summary>
    private async Task<bool> TryEnterGateForChatAsync(CancellationToken cancellationToken)
    {
        if (!await _turnGate.WaitAsync(0, CancellationToken.None))
        {
            return await WaitForGateForChatAsync(cancellationToken);
        }

        return IsStillWantedAfterEntering(cancellationToken);
    }

    private async Task<bool> WaitForGateForChatAsync(CancellationToken cancellationToken)
    {
        string? replacedStatusMessage = null;
        var showsWaitingMessage = false;
        lock (_stateLock)
        {
            if (_scheduler is not null)
            {
                replacedStatusMessage = StatusMessage;
                StatusMessage = WaitingStatusMessage;
                showsWaitingMessage = true;
            }
        }

        try
        {
            await _turnGate.WaitAsync(cancellationToken);
            if (IsStillWantedAfterEntering(cancellationToken))
            {
                return true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The waiting message is removed below.
        }

        lock (_stateLock)
        {
            // Only undone while still shown: the background run may have replaced it meanwhile.
            if (showsWaitingMessage && _scheduler is not null && StatusMessage == WaitingStatusMessage)
            {
                StatusMessage = replacedStatusMessage;
            }
        }

        return false;
    }

    // A stop waits for the gate only once, so a chat that enters after it sees its cancelled token and must not run.
    private bool IsStillWantedAfterEntering(CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        _turnGate.Release();
        return false;
    }
}
