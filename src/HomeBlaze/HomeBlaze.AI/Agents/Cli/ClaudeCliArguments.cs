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
    bool PersistSession)
{
    /// <summary>
    /// The backend tools the turn may use, such as <c>web_search</c>; names without a CLI tool are ignored.
    /// </summary>
    public IReadOnlyList<string> BackendTools { get; init; } = [];
}

/// <summary>
/// Builds the command line of the Claude CLI for agent turns.
/// </summary>
public static class ClaudeCliArguments
{
    /// <summary>The MCP server name, which prefixes its tools as <c>mcp__homeblaze__</c>.</summary>
    public const string McpServerName = "homeblaze";

    // Only web tools: built-in tools with host access (shell, files, agents) are never offered.
    private static readonly (string BackendTool, string CliTool)[] CliTools =
    [
        (AgentBackendTool.WebSearch.Name, "WebSearch"),
        (AgentBackendTool.WebFetch.Name, "WebFetch")
    ];

    /// <summary>The backend tool name of a CLI built-in tool, or null when it is not one of the mapped tools.</summary>
    public static string? GetBackendToolName(string cliToolName)
    {
        foreach (var (backendTool, cliTool) in CliTools)
        {
            if (cliTool == cliToolName)
            {
                return backendTool;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the arguments. The run is isolated from the host user's setup: no built-in tools except the allowed web
    /// tools, only the HomeBlaze MCP server, no user, project or local settings, hooks, plugins or CLAUDE.md files.
    /// </summary>
    public static IReadOnlyList<string> Build(ClaudeCliOptions options)
    {
        // Not --bare, which would isolate the run too but disables the OAuth login.
        var cliTools = CliTools
            .Where(tool => options.BackendTools.Contains(tool.BackendTool))
            .Select(tool => tool.CliTool)
            .ToList();

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
            // Comma-separated in one value each: the built-in tools, and the tools that run without a permission prompt.
            "--tools", string.Join(',', cliTools),
            "--strict-mcp-config",
            "--mcp-config", options.McpConfigurationPath,
            "--allowedTools", string.Join(',', cliTools.Prepend("mcp__" + McpServerName)),
            // No source at all: "project" would load CLAUDE.md from the working directory and every folder above it.
            "--setting-sources", string.Empty
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
