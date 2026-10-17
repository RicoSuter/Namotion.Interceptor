using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HomeBlaze.AI.Agents.ClaudeCli;

/// <summary>One CLI invocation.</summary>
/// <param name="FileName">Path or name of the CLI executable.</param>
/// <param name="Arguments">Arguments, passed without a shell.</param>
/// <param name="WorkingDirectory">Working directory of the process.</param>
/// <param name="StandardInput">Written to standard input, which is then closed.</param>
internal sealed record ClaudeCliInvocation(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, string StandardInput);

/// <summary>What a CLI invocation produced.</summary>
/// <param name="ExitCode">Exit code of the process.</param>
/// <param name="SessionId">Session id from the result, or from the init line when no result was written.</param>
/// <param name="Result">The result line, or null when none was written.</param>
/// <param name="ErrorCode">Error code of the last assistant line that carried one, for example <c>authentication_failed</c>.</param>
/// <param name="RateLimit">The last rate limit line.</param>
/// <param name="StandardError">Standard error, truncated to a size limit.</param>
/// <param name="StreamedText">
/// The streamed answer text, with a blank line between text segments of different content blocks, or null when no
/// text was streamed.
/// </param>
internal sealed record ClaudeCliRunResult(
    int ExitCode,
    string? SessionId,
    ClaudeResultEvent? Result,
    string? ErrorCode,
    ClaudeRateLimitEvent? RateLimit,
    string StandardError,
    string? StreamedText);

/// <summary>
/// Runs the Claude CLI, streams its text deltas and collects its result. Cancellation kills the whole process tree.
/// </summary>
internal static class ClaudeCliProcess
{
    private const int MaxStandardErrorLength = 16 * 1024;

    private static readonly TimeSpan KilledProcessExitTimeout = TimeSpan.FromSeconds(5);

    // In -p mode the CLI always uses an API key, provider or gateway from the environment over the subscription login,
    // so it would bill the API instead of the subscription. CLAUDE_CODE_OAUTH_TOKEN is a subscription token and is kept.
    private static readonly string[] RemovedEnvironmentVariablePrefixes = ["ANTHROPIC_", "CLAUDE_CODE_USE_"];

    // These reroute requests or hand the credentials and billing to a host, like the prefixes above.
    private static readonly string[] RemovedEnvironmentVariableNames =
    [
        "CLAUDE_CODE_API_BASE_URL",
        "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR",
        "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST",
        "CLAUDE_CODE_HOST_AUTH_ENV_VAR",
        "CLAUDE_CODE_HOST_CREDS_FILE"
    ];

    // The CLI reads Anthropic profiles from this directory instead of ~/.config/anthropic, so an empty one leaves none.
    private const string ConfigDirectoryVariable = "ANTHROPIC_CONFIG_DIR";

    private const string ConfigDirectoryName = ".anthropic-config";

    // The agent route is on this address; a proxy from HTTP_PROXY or HTTPS_PROXY must not receive its calls and token.
    private const string LoopbackAddress = "127.0.0.1";

    // The init line names this API key source when the CLI uses none, as with the subscription login.
    private const string NoApiKeySource = "none";

    // Windows matches variable names case-insensitively, so the CLI would read anthropic_api_key there too.
    private static readonly StringComparison EnvironmentVariableComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Runs the invocation to completion.
    /// </summary>
    /// <param name="invocation">The invocation.</param>
    /// <param name="progress">Receives the streamed text.</param>
    /// <param name="backendToolCalls">
    /// Records each finished call of a built-in CLI tool, such as <c>WebSearch</c> as <c>web_search</c>, when its result
    /// arrives, or null to record none; calls of MCP tools are not recorded here, since the MCP route records them.
    /// </param>
    /// <param name="logger">Receives a warning when the init line names no API key source, so the billing check cannot run.</param>
    /// <param name="cancellationToken">Kills the process tree when cancelled.</param>
    /// <exception cref="AgentTurnException">
    /// The CLI could not be started, or its init line names an API key source, so it would bill the API instead of the
    /// subscription; the process tree has then been killed (both <see cref="AgentFailureKind.Configuration"/>).
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the process tree has been killed.</exception>
    public static async Task<ClaudeCliRunResult> RunAsync(
        ClaudeCliInvocation invocation,
        IProgress<AgentTurnUpdate> progress,
        AgentToolSet? backendToolCalls,
        ILogger logger,
        CancellationToken cancellationToken)
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

            var output = new OutputCollector(backendToolCalls, logger, invocation.WorkingDirectory);
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

        if (IsBatchFile(invocation.FileName))
        {
            throw new AgentTurnException(
                AgentFailureKind.Configuration,
                $"The Claude CLI path '{invocation.FileName}' is a batch file, which cannot receive the arguments safely. Set CliPath to the claude executable (claude.exe) instead of the npm shim.");
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

        foreach (var name in startInfo.Environment.Keys.Where(name => IsRemovedEnvironmentVariable(name, EnvironmentVariableComparison)).ToList())
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment[ConfigDirectoryVariable] = CreateConfigDirectory(invocation.WorkingDirectory);

        // Tools differ in which spelling they read, so both get the exclusion; on Windows they are one variable.
        startInfo.Environment.TryGetValue("NO_PROXY", out var upperCaseNoProxy);
        startInfo.Environment.TryGetValue("no_proxy", out var lowerCaseNoProxy);
        startInfo.Environment["NO_PROXY"] = WithLoopbackProxyExclusion(upperCaseNoProxy ?? lowerCaseNoProxy);
        startInfo.Environment["no_proxy"] = WithLoopbackProxyExclusion(lowerCaseNoProxy ?? upperCaseNoProxy);

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

    private static string CreateConfigDirectory(string workingDirectory)
    {
        // Recreated for every run, so nothing left in it, such as a profile, applies.
        var path = Path.Combine(workingDirectory, ConfigDirectoryName);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentTurnException(
                AgentFailureKind.Configuration,
                $"The Anthropic config folder '{path}' of the Claude CLI could not be emptied ({exception.Message}). Make the working directory writable for the HomeBlaze user, or remove the folder.",
                innerException: exception);
        }

        return path;
    }

    /// <summary>Adds 127.0.0.1 to a comma-separated proxy exclusion list unless it is listed already.</summary>
    /// <param name="value">The list, or null when the variable is not set.</param>
    internal static string WithLoopbackProxyExclusion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LoopbackAddress;
        }

        foreach (var entry in value.Split(','))
        {
            if (entry.Trim() == LoopbackAddress)
            {
                return value;
            }
        }

        return value + "," + LoopbackAddress;
    }

    /// <summary>Whether the variable is removed from the CLI environment because it would reroute requests or switch billing away from the subscription.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="comparison">How names compare: case-insensitive on Windows, ordinal elsewhere.</param>
    internal static bool IsRemovedEnvironmentVariable(string name, StringComparison comparison)
    {
        foreach (var prefix in RemovedEnvironmentVariablePrefixes)
        {
            if (name.StartsWith(prefix, comparison))
            {
                return true;
            }
        }

        foreach (var removedName in RemovedEnvironmentVariableNames)
        {
            if (name.Equals(removedName, comparison))
            {
                return true;
            }
        }

        return false;
    }

    // Windows starts a batch file through cmd.exe, which parses the arguments again by rules no quoting can satisfy.
    // Windows also drops trailing dots and spaces from a file name, so "claude.cmd." still runs the batch file.
    private static bool IsBatchFile(string fileName)
    {
        var extension = Path.GetExtension(fileName.AsSpan().TrimEnd(" ."));
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
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

    private sealed class OutputCollector(AgentToolSet? backendToolCalls, ILogger logger, string workingDirectory)
    {
        private const string McpToolPrefix = "mcp__";

        private readonly AgentAnswerText _answer = new();
        private Dictionary<string, ClaudeToolUse>? _pendingBuiltInToolUses;
        private string? _sessionId;
        private string? _errorCode;
        private ClaudeRateLimitEvent? _rateLimit;
        private ClaudeResultEvent? _result;

        public void Apply(ClaudeStreamEvent? streamEvent, IProgress<AgentTurnUpdate> progress)
        {
            switch (streamEvent)
            {
                case ClaudeToolUseEvent toolUses when backendToolCalls is not null:
                    RememberBuiltInToolUses(toolUses.ToolUses);
                    break;
                case ClaudeToolResultEvent results when _pendingBuiltInToolUses is not null:
                    ReportBuiltInToolCalls(results.Results);
                    break;
                case ClaudeInitEvent init:
                    ThrowIfBillingTheApi(init.ApiKeySource);
                    if (init.ApiKeySource is null)
                    {
                        logger.LogWarning(
                            "The init line of the Claude CLI in {WorkingDirectory} names no API key source, so HomeBlaze cannot check that the CLI uses the subscription login instead of billing the API.",
                            workingDirectory);
                    }

                    _sessionId = init.SessionId;
                    break;
                case ClaudeBlockStartEvent:
                    _answer.StartSegment();
                    break;
                case ClaudeTextDeltaEvent delta:
                    _answer.Append(delta.Text, progress);
                    break;
                case ClaudeRateLimitEvent limit:
                    _rateLimit = limit;
                    break;
                case ClaudeAssistantErrorEvent error:
                    _errorCode = error.Code;
                    break;
                case ClaudeResultEvent final:
                    _result = final;
                    break;
            }
        }

        public ClaudeCliRunResult ToResult(int exitCode, string standardError) =>
            new(exitCode, _result?.SessionId ?? _sessionId, _result, _errorCode, _rateLimit, standardError, _answer.Text);

        // The environment filter cannot see a Console login or an apiKeyHelper of the managed settings. The init line
        // comes before the first model request; throwing kills the process before that request completes.
        private static void ThrowIfBillingTheApi(string? apiKeySource)
        {
            if (apiKeySource is not null && apiKeySource != NoApiKeySource)
            {
                throw new AgentTurnException(
                    AgentFailureKind.Configuration,
                    $"The Claude CLI is not using the Claude subscription login: it takes an API key from '{apiKeySource}', which would bill the API. Remove that key source, for example a Console login with claude /logout or an apiKeyHelper in the managed settings, and log in with the subscription.");
            }
        }

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

        private void ReportBuiltInToolCalls(IReadOnlyList<ClaudeToolResult> results)
        {
            foreach (var result in results)
            {
                if (_pendingBuiltInToolUses!.Remove(result.ToolUseId, out var toolUse))
                {
                    backendToolCalls!.RecordBackendCall(
                        ClaudeCliArguments.GetBackendToolName(toolUse.Name) ?? toolUse.Name, toolUse.Arguments, result.Content, result.IsError);
                }
            }
        }
    }
}
