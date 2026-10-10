using System.Buffers;
using System.Text;
using System.Text.Json;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>
/// The settings of one CLI invocation.
/// </summary>
/// <param name="SystemPrompt">Replaces Claude Code's own system prompt.</param>
/// <param name="McpConfigurationPath">File with the HomeBlaze MCP server and the turn token.</param>
/// <param name="Model">Model alias or id, null for the CLI default.</param>
/// <param name="Effort">Effort, null for the model default.</param>
/// <param name="FallbackModel">Model used when the main model is overloaded.</param>
/// <param name="ResumeSessionId">Chat session to continue.</param>
/// <param name="PersistSession">False for background runs, which leave no session behind.</param>
public sealed record ClaudeCliOptions(
    string SystemPrompt,
    string McpConfigurationPath,
    string? Model,
    AgentEffort? Effort,
    string? FallbackModel,
    string? ResumeSessionId,
    bool PersistSession);

/// <summary>
/// Builds the command line of the Claude CLI for agent turns.
/// </summary>
public static class ClaudeCliArguments
{
    /// <summary>The MCP server name, which prefixes its tools as <c>mcp__homeblaze__</c>.</summary>
    public const string McpServerName = "homeblaze";

    /// <summary>
    /// Builds the arguments. The run is isolated from the host user's setup: no built-in tools, only the HomeBlaze
    /// MCP server, no user settings, hooks or plugins. <c>--bare</c> is not used because it disables OAuth login.
    /// </summary>
    public static IReadOnlyList<string> Build(ClaudeCliOptions options)
    {
        // --tools, --mcp-config and --allowedTools take several values and are terminated by the next flag, so each must
        // stay followed by a flag and no positional prompt may be appended; the prompt goes through standard input.
        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--system-prompt", options.SystemPrompt,
            // The default snapshots the prompt per session, which would hide later memory edits from a resumed chat.
            "--system-prompt-snapshot", "off",
            "--tools", string.Empty,
            "--strict-mcp-config",
            "--mcp-config", options.McpConfigurationPath,
            "--allowedTools", "mcp__" + McpServerName,
            "--setting-sources", "project"
        };

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.AddRange(["--model", options.Model]);
        }

        if (options.Effort is { } effort)
        {
            arguments.AddRange(["--effort", effort.ToWireValue()]);
        }

        if (!string.IsNullOrWhiteSpace(options.FallbackModel))
        {
            arguments.AddRange(["--fallback-model", options.FallbackModel]);
        }

        if (!string.IsNullOrWhiteSpace(options.ResumeSessionId))
        {
            arguments.AddRange(["--resume", options.ResumeSessionId]);
        }

        if (!options.PersistSession)
        {
            arguments.Add("--no-session-persistence");
        }

        return arguments;
    }

    /// <summary>
    /// Builds the MCP configuration JSON pointing the CLI at the agent route with the turn's bearer token.
    /// </summary>
    public static string BuildMcpConfiguration(Uri endpoint, string token)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("mcpServers");
            writer.WriteStartObject(McpServerName);
            writer.WriteString("type", "http");
            writer.WriteString("url", endpoint.AbsoluteUri);
            writer.WriteStartObject("headers");
            writer.WriteString("Authorization", "Bearer " + token);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
