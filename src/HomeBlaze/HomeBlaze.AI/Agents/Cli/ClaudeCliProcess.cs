using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>One CLI invocation.</summary>
/// <param name="FileName">Path or name of the CLI executable.</param>
/// <param name="Arguments">Arguments, passed without a shell.</param>
/// <param name="WorkingDirectory">Working directory of the process.</param>
/// <param name="StandardInput">Written to standard input, which is then closed.</param>
public sealed record ClaudeCliInvocation(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string StandardInput);

/// <summary>What a CLI invocation produced.</summary>
/// <param name="ExitCode">Exit code of the process.</param>
/// <param name="SessionId">Session id from the result, or from the init line when no result was written.</param>
/// <param name="ResultText">Text of the result line.</param>
/// <param name="IsError">Whether the result line is marked as an error.</param>
/// <param name="ResultSubtype">Subtype of the result line, for example <c>success</c> or <c>error_during_execution</c>.</param>
/// <param name="ErrorCode">Error code of the last assistant line that carried one, for example <c>authentication_failed</c>.</param>
/// <param name="Usage">Token usage of the result line.</param>
/// <param name="RateLimit">The last reported subscription utilization.</param>
/// <param name="StandardError">Standard error, truncated to a size limit.</param>
/// <param name="StreamedText">
/// The streamed answer text, with a blank line between text segments of different content blocks, or null when no
/// text was streamed.
/// </param>
/// <param name="LastStreamedSegment">The last streamed text segment, or null when no text was streamed.</param>
public sealed record ClaudeCliRunResult(
    int ExitCode,
    string? SessionId,
    string? ResultText,
    bool IsError,
    string? ResultSubtype,
    string? ErrorCode,
    AgentUsage? Usage,
    AgentRateLimit? RateLimit,
    string StandardError,
    string? StreamedText = null,
    string? LastStreamedSegment = null);

/// <summary>
/// Runs the Claude CLI, streams its text deltas and collects its result. Cancellation kills the whole process tree.
/// </summary>
public static class ClaudeCliProcess
{
    private const int MaxStandardErrorLength = 16 * 1024;

    private static readonly TimeSpan KilledProcessExitTimeout = TimeSpan.FromSeconds(5);

    // In -p mode the CLI always uses an API key or provider from the environment over the subscription login, so it
    // would bill the API instead of the subscription. CLAUDE_CODE_OAUTH_TOKEN is a subscription token and is kept.
    private static readonly string[] RemovedEnvironmentVariables =
    [
        "ANTHROPIC_API_KEY",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_BASE_URL",
        "CLAUDE_CODE_USE_BEDROCK",
        "CLAUDE_CODE_USE_VERTEX",
        "CLAUDE_CODE_USE_FOUNDRY",
        "ANTHROPIC_PROFILE",
        "ANTHROPIC_FEDERATION_RULE_ID",
        "ANTHROPIC_ORGANIZATION_ID"
    ];

    /// <summary>
    /// Runs the invocation to completion.
    /// </summary>
    /// <param name="invocation">The invocation.</param>
    /// <param name="progress">Receives the streamed text.</param>
    /// <param name="cancellationToken">Kills the process tree when cancelled.</param>
    /// <param name="backendToolCalls">
    /// Records each finished call of a built-in CLI tool, such as <c>WebSearch</c> as <c>web_search</c>, when its result
    /// arrives; calls of MCP tools are not recorded here, since the MCP route records them.
    /// </param>
    /// <exception cref="AgentTurnException">The CLI could not be started (<see cref="AgentFailureKind.Configuration"/>).</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the process tree has been killed.</exception>
    public static async Task<ClaudeCliRunResult> RunAsync(
        ClaudeCliInvocation invocation,
        IProgress<AgentTurnUpdate> progress,
        CancellationToken cancellationToken,
        AgentToolSet? backendToolCalls = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = StartProcess(invocation);

        Task<string>? standardErrorTask = null;
        Task? standardInputTask = null;
        try
        {
            // Killing closes the pipes, so the pending reads end even where a pipe read does not observe the token.
            using var killOnCancellation = cancellationToken.Register(static state => KillProcessTree((Process)state!), process);

            // Standard error is drained and standard input written concurrently with reading standard output, so a
            // full pipe on either side cannot block the CLI while this method waits on another stream.
            standardErrorTask = ReadLimitedAsync(process.StandardError, cancellationToken);
            standardInputTask = WriteStandardInputAsync(process.StandardInput, invocation.StandardInput, cancellationToken);

            var output = new OutputCollector(backendToolCalls);
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                output.Apply(ClaudeStreamJsonParser.Parse(line), progress);
            }

            // End of output can also come from the kill on cancellation, which must not read as a completed run.
            cancellationToken.ThrowIfCancellationRequested();
            await standardInputTask;
            await process.WaitForExitAsync(cancellationToken);
            var standardError = await standardErrorTask.WaitAsync(cancellationToken);
            return output.ToResult(process.ExitCode, standardError);
        }
        finally
        {
            if (!process.HasExited)
            {
                KillProcessTree(process);
                try
                {
                    // Waiting reaps the killed process before it is disposed and lets the pipe readers reach the end.
                    await process.WaitForExitAsync(CancellationToken.None).WaitAsync(KilledProcessExitTimeout, CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    // The kill was requested; disposing the process does not depend on it having exited.
                }
            }

            // The helpers end once the pipes reach the end or the token is cancelled; the bound only guards against a
            // descendant that escaped the kill and still holds a pipe open.
            await Task.WhenAll(standardErrorTask ?? Task.CompletedTask, standardInputTask ?? Task.CompletedTask)
                .WaitAsync(KilledProcessExitTimeout, CancellationToken.None)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            ObserveFailure(standardErrorTask);
            ObserveFailure(standardInputTask);

            // Releases the pipe handles now instead of leaving them to finalizers.
            DisposeIgnoringBrokenPipe(process.StandardInput);
            DisposeIgnoringBrokenPipe(process.StandardOutput);
            DisposeIgnoringBrokenPipe(process.StandardError);
        }
    }

    private static Process StartProcess(ClaudeCliInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.FileName))
        {
            throw new AgentTurnException(AgentFailureKind.Configuration, "The Claude CLI path is not set.");
        }

        if (!Directory.Exists(invocation.WorkingDirectory))
        {
            throw new AgentTurnException(
                AgentFailureKind.Configuration,
                $"The working directory '{invocation.WorkingDirectory}' of the Claude CLI does not exist.");
        }

        var startInfo = new ProcessStartInfo(invocation.FileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = invocation.WorkingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in RemovedEnvironmentVariables)
        {
            startInfo.Environment.Remove(name);
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            return process;
        }
        catch (Exception exception)
        {
            process.Dispose();
            if (exception is not Win32Exception)
            {
                throw;
            }

            throw new AgentTurnException(
                AgentFailureKind.Configuration,
                $"The Claude CLI '{invocation.FileName}' could not be started ({exception.Message}). Install Claude Code for the HomeBlaze user and log in, or set the CLI path.",
                innerException: exception);
        }
    }

    private static void DisposeIgnoringBrokenPipe(IDisposable stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
            // Disposing standard input flushes what is left, which fails when the CLI has exited.
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or AggregateException)
        {
            // The process exited in the meantime, or a descendant could not be killed; neither can be acted on here.
        }
    }

    private static void ObserveFailure(Task? task)
    {
        if (task is { IsCompletedSuccessfully: false })
        {
            task.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task WriteStandardInputAsync(StreamWriter writer, string input, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(input.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // The CLI exited before reading its input; its output and exit code explain why.
        }
        finally
        {
            try
            {
                writer.Close();
            }
            catch (IOException)
            {
                // Closing flushes what is left, which fails the same way when the CLI has exited.
            }
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            {
                // Reading continues past the limit, because a CLI blocked on a full standard error pipe never exits.
                if (builder.Length < MaxStandardErrorLength)
                {
                    builder.Append(buffer, 0, Math.Min(read, MaxStandardErrorLength - builder.Length));
                }
            }
        }
        catch (IOException)
        {
            // The pipe broke; what was read so far is the diagnostic.
        }

        return builder.ToString();
    }

    // A mutable struct local of the async caller, so collecting the output lines allocates nothing beyond the text.
    private struct OutputCollector
    {
        private const string SegmentSeparator = "\n\n";
        private const string McpToolPrefix = "mcp__";

        private readonly AgentToolSet? _backendToolCalls;
        private Dictionary<string, ClaudeToolUse>? _pendingBuiltInToolUses;
        private StringBuilder? _text;
        private int _lastSegmentStart;
        private bool _isNewBlock;
        private string? _sessionId;
        private string? _errorCode;
        private AgentRateLimit? _rateLimit;
        private ClaudeResultEvent? _result;

        public OutputCollector(AgentToolSet? backendToolCalls)
        {
            _backendToolCalls = backendToolCalls;
        }

        public void Apply(ClaudeStreamEvent? streamEvent, IProgress<AgentTurnUpdate> progress)
        {
            switch (streamEvent)
            {
                case ClaudeToolUseEvent toolUses when _backendToolCalls is not null:
                    RememberBuiltInToolUses(toolUses.ToolUses);
                    break;
                case ClaudeToolResultEvent results when _pendingBuiltInToolUses is not null:
                    ReportBuiltInToolCalls(results.Results);
                    break;
                case ClaudeInitEvent init:
                    _sessionId = init.SessionId;
                    break;
                case ClaudeBlockStartEvent:
                    _isNewBlock = true;
                    break;
                case ClaudeTextDeltaEvent delta:
                    AppendText(delta.Text, progress);
                    break;
                case ClaudeRateLimitEvent limit:
                    _rateLimit = limit.RateLimit;
                    break;
                case ClaudeAssistantErrorEvent error:
                    _errorCode = error.Code;
                    break;
                case ClaudeResultEvent final:
                    _result = final;
                    break;
            }
        }

        public readonly ClaudeCliRunResult ToResult(int exitCode, string standardError) => new(
            exitCode,
            _result?.SessionId ?? _sessionId,
            _result?.Text,
            _result?.IsError ?? false,
            _result?.Subtype,
            _errorCode,
            _result?.Usage,
            _rateLimit,
            standardError,
            _text?.ToString(),
            _text?.ToString(_lastSegmentStart, _text.Length - _lastSegmentStart));

        private void RememberBuiltInToolUses(IReadOnlyList<ClaudeToolUse> toolUses)
        {
            foreach (var toolUse in toolUses)
            {
                if (!toolUse.Name.StartsWith(McpToolPrefix, StringComparison.Ordinal))
                {
                    (_pendingBuiltInToolUses ??= [])[toolUse.Id] = toolUse;
                }
            }
        }

        private readonly void ReportBuiltInToolCalls(IReadOnlyList<ClaudeToolResult> results)
        {
            foreach (var result in results)
            {
                if (_pendingBuiltInToolUses!.Remove(result.ToolUseId, out var toolUse))
                {
                    _backendToolCalls!.RecordBackendCall(
                        ClaudeCliArguments.GetBackendToolName(toolUse.Name) ?? toolUse.Name, toolUse.Arguments, result.Content, result.IsError);
                }
            }
        }

        // The separator is streamed too, so the live text and the stored answer read the same.
        private void AppendText(string text, IProgress<AgentTurnUpdate> progress)
        {
            if (text.Length == 0)
            {
                return;
            }

            _text ??= new StringBuilder();
            if (_isNewBlock && _text.Length > 0)
            {
                _text.Append(SegmentSeparator);
                _lastSegmentStart = _text.Length;
                progress.Report(new AgentTextDelta(SegmentSeparator));
            }

            _isNewBlock = false;
            _text.Append(text);
            progress.Report(new AgentTextDelta(text));
        }
    }
}
