using System.ComponentModel;
using System.Text.Json;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>
/// An agent that runs on the Claude subscription of the user HomeBlaze runs as, through the locally installed
/// and logged-in <c>claude</c> CLI. Claude Code runs the tool loop and reaches the graph tools through the
/// scoped agent MCP route.
/// </summary>
[Category("AI")]
[Description("AI agent running on your Claude subscription through the locally installed claude CLI.")]
[InterceptorSubject]
public partial class ClaudeCliAgent : LlmAgentBase
{
    private readonly ILogger<ClaudeCliAgent> _logger;

    // Only read and written while the base class holds the turn gate.
    private string? _sessionId;

    public ClaudeCliAgent(AgentServices services, ILogger<ClaudeCliAgent> logger)
        : base(services, logger, AgentBackendTool.WebTools)
    {
        _logger = logger;
        CliPath = "claude";
    }

    /// <summary>The CLI executable; a bare name is resolved through PATH.</summary>
    [Configuration]
    public partial string CliPath { get; set; }

    /// <summary>The model used when the main model is overloaded.</summary>
    [Configuration]
    public partial string? FallbackModel { get; set; }

    protected override async Task OnStartingAsync(CancellationToken cancellationToken)
    {
        _sessionId = await ReadSessionAsync(
            json => JsonSerializer.Deserialize(json, AgentJsonContext.Default.ClaudeCliSessionFile)?.SessionId, cancellationToken);
    }

    protected override async Task ResetSessionAsync(CancellationToken cancellationToken)
    {
        _sessionId = null;
        await WriteSessionAsync(null, cancellationToken);
    }

    protected override async Task<AgentTurnResult> ExecuteTurnAsync(
        AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        var endpoint = Services.EndpointAddress.GetAgentEndpoint()
            ?? throw new AgentTurnException(AgentFailureKind.Transient, "The address of the web server is not known yet.");

        Directory.CreateDirectory(WorkingDirectory);
        using var registration = Services.TokenRegistry.Register(request.Tools);
        var turnId = Guid.NewGuid().ToString("N");
        var setup = new ClaudeCliTurnSetup(
            Path.Combine(WorkingDirectory, ".system-prompt-" + turnId + ".md"),
            Path.Combine(WorkingDirectory, ".mcp-" + turnId + ".json"),
            GetModelRoundLimit());
        try
        {
            await OwnerOnlyFile.WriteAsync(setup.SystemPromptPath, request.SystemPrompt, cancellationToken);
            await OwnerOnlyFile.WriteAsync(setup.McpConfigurationPath, ClaudeCliArguments.BuildMcpConfiguration(endpoint, registration.Token), cancellationToken);

            var isChat = request.Kind == AgentTurnKind.Chat;
            var result = await RunCliAsync(request, setup, isChat ? _sessionId : null, progress, cancellationToken);
            if (isChat && _sessionId is not null && ClaudeCliFailure.IsSessionMissing(result))
            {
                await ResetSessionAsync(cancellationToken);
                await AppendDividerAsync("Session restarted");
                result = await RunCliAsync(request, setup, null, progress, cancellationToken);
            }

            if (ClaudeCliFailure.Classify(result, setup.MaxTurns) is { } failure)
            {
                throw failure;
            }

            if (isChat)
            {
                await RememberSessionAsync(result.SessionId, cancellationToken);
            }

            return ToTurnResult(result);
        }
        finally
        {
            DeleteTurnFile(setup.SystemPromptPath);
            DeleteTurnFile(setup.McpConfigurationPath);
        }
    }

    // The result line holds only the text after the last tool call; the timeline keeps the whole answer.
    private static AgentTurnResult ToTurnResult(ClaudeCliRunResult result)
    {
        var finalText = result.ResultText ?? string.Empty;
        var text = string.IsNullOrEmpty(result.StreamedText) ? finalText : result.StreamedText;
        return new AgentTurnResult(text, result.Usage, result.RateLimit) { FinalText = finalText };
    }

    private async Task RememberSessionAsync(string? sessionId, CancellationToken cancellationToken)
    {
        if (sessionId is null || sessionId == _sessionId)
        {
            return;
        }

        _sessionId = sessionId;
        await WriteSessionAsync(
            JsonSerializer.Serialize(new ClaudeCliSessionFile(sessionId), AgentJsonContext.Default.ClaudeCliSessionFile),
            cancellationToken);
    }

    private Task<ClaudeCliRunResult> RunCliAsync(
        AgentTurnRequest request, ClaudeCliTurnSetup setup, string? resumeSessionId, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        var options = new ClaudeCliOptions(
            setup.SystemPromptPath,
            setup.McpConfigurationPath,
            request.Model,
            request.Effort,
            FallbackModel,
            resumeSessionId,
            PersistSession: request.Kind == AgentTurnKind.Chat)
        {
            BackendTools = request.BackendTools,
            MaxTurns = setup.MaxTurns
        };
        var invocation = new ClaudeCliInvocation(CliPath, ClaudeCliArguments.Build(options), WorkingDirectory, request.UserMessage);
        // Built-in tool calls are recorded only when backend tools are allowed; the CLI gets no other built-in tool.
        return ClaudeCliProcess.RunAsync(invocation, progress, cancellationToken, request.BackendTools.Count > 0 ? request.Tools : null);
    }

    private void DeleteTurnFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Must not replace the turn's outcome; the file is owner-only and the token in it is revoked when the turn ends.
            _logger.LogWarning(exception, "The turn file {Path} of agent {Agent} could not be deleted.", path, Title);
        }
    }

    /// <summary>What the runs of one turn share: its owner-only files and its model round limit.</summary>
    private sealed record ClaudeCliTurnSetup(string SystemPromptPath, string McpConfigurationPath, int MaxTurns);
}

/// <summary>The session file of a <see cref="ClaudeCliAgent"/>.</summary>
internal sealed record ClaudeCliSessionFile(string SessionId);
