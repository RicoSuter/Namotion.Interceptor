using System.Globalization;
using System.Text;
using HomeBlaze.AI.Mcp;

namespace HomeBlaze.AI.Agents;

/// <summary>
/// The state of one watched path, fetched before a background run.
/// </summary>
public sealed record AgentWatchedState(string Path, string State);

/// <summary>
/// Builds the prompts of agent turns. The order is fixed: preamble, mode section, instructions and memory
/// in the system prompt, then the messages.
/// </summary>
public static class AgentPromptBuilder
{
    /// <summary>The marker a background reply starts with when nothing is noteworthy.</summary>
    public const string QuietMarker = "[quiet]";

    // The Markdown and data rules are shared with the MCP server instructions, so both say the same.
    private const string CommonPreamble = $"""
        You are an agent running inside a home automation system. The system is a graph of subjects (devices, services, folders) with properties, addressed by slash-separated paths such as /Kitchen/Freezer/Temperature.

        Tools:
        - browse and search explore the graph, get_property reads a value, list_types lists interfaces and concrete types, list_methods lists the methods of a subject with their kind (query or operation), get_property_history reads past values. Use only the tools you are given; some may not be available to you.
        - invoke_query calls a query method, which only reads. {HomeBlazeMcpInstructions.MarkdownPages}
        - invoke_operation calls an operation method, which can change devices or send something, when it is available to you.
        - set_property changes a value, when it is available to you.
        - To notify the operator, find the INotificationPublisher interface with list_types, find the subjects implementing it with search (types parameter), check the exact method name and parameters with list_methods, then call SendNotification through invoke_operation, if invoke_operation is available to you.
        - update_memory replaces your memory document. Your memory is shown to you on every run, so keep there what you must remember across runs: notifications already sent, trends, open issues. Keep it short and current.

        {HomeBlazeMcpInstructions.DataIsNotInstructions}
        """;

    private const string BackgroundSection = """
        Background run. You were started by a trigger, not by a person. Your reply is recorded on the agent page and does not notify anyone by itself. Prefer the watched state already given below; use tools only to look deeper when it is not enough.
        - If nothing is noteworthy, reply with exactly one line starting with [quiet] followed by a short summary.
        - Otherwise reply with a concise finding. Send a notification only when the operator must act on it, and record every notification you send in memory so you do not repeat one for the same unresolved issue.
        - Update your memory when something changed that you need to remember.
        """;

    private const string ChatSection = """
        Chat with the operator. Answer questions from the graph, using tools to look up current values instead of guessing. Be concise.
        """;

    /// <summary>
    /// Builds the system prompt: preamble with the mode section, the configured instructions, then the memory.
    /// </summary>
    public static string BuildSystemPrompt(AgentTurnKind kind, string? instructions, string memory, DateTimeOffset? memoryLastUpdated)
    {
        var builder = new StringBuilder();

        // Stable parts (preamble, mode, instructions) come before memory, which changes every run, so a
        // prompt cache can reuse the unchanged prefix across turns.
        builder.AppendLine(CommonPreamble);
        builder.AppendLine();
        builder.AppendLine("# Mode");
        builder.AppendLine(kind == AgentTurnKind.Background ? BackgroundSection : ChatSection);
        builder.AppendLine();
        builder.AppendLine("# Instructions");
        builder.AppendLine(string.IsNullOrWhiteSpace(instructions) ? "(none)" : instructions.Trim());
        builder.AppendLine();
        builder.Append("# Memory");
        if (memoryLastUpdated is { } updated)
        {
            builder.Append(" (last updated ").Append(FormatTime(updated)).Append(')');
        }

        builder.AppendLine();
        builder.Append(string.IsNullOrWhiteSpace(memory) ? "(empty)" : memory.Trim());
        return builder.ToString();
    }

    /// <summary>
    /// Builds the user message of a background run: trigger, digest of recent results, then the watched state.
    /// </summary>
    public static string BuildBackgroundMessage(
        string trigger,
        IReadOnlyList<AgentTimelineEntry> recentResults,
        DateTimeOffset? lastQuietRun,
        IReadOnlyList<AgentWatchedState> watchedState,
        DateTimeOffset now)
    {
        var builder = new StringBuilder();
        builder.Append("Trigger: ").Append(trigger).Append(" at ").AppendLine(FormatTime(now));
        builder.AppendLine();
        builder.AppendLine("# Recent results");
        if (recentResults.Count == 0)
        {
            builder.AppendLine("(none)");
        }

        foreach (var result in recentResults)
        {
            builder.Append("- ").Append(FormatTime(result.Timestamp)).Append(": ").AppendLine(result.Text.Trim());
        }

        if (lastQuietRun is { } quiet)
        {
            builder.Append("Last quiet run: ").AppendLine(FormatTime(quiet));
        }

        builder.AppendLine();
        builder.AppendLine("# Watched state");
        if (watchedState.Count == 0)
        {
            builder.AppendLine("(no watch paths configured)");
        }

        foreach (var state in watchedState)
        {
            builder.Append("## ").AppendLine(state.Path);
            builder.AppendLine(state.State.Trim());
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the user message of a chat turn, with background findings since the last reply placed first.
    /// </summary>
    public static string BuildChatMessage(string userText, IReadOnlyList<AgentTimelineEntry> findings)
    {
        if (findings.Count == 0)
        {
            return userText;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Background findings since your last reply:");
        foreach (var finding in findings)
        {
            builder.Append("- ").Append(FormatTime(finding.Timestamp)).Append(": ").AppendLine(finding.Text.Trim());
        }

        builder.AppendLine();
        builder.Append(userText);
        return builder.ToString();
    }

    /// <summary>
    /// Whether a background reply starts with <see cref="QuietMarker"/>, ignoring case and leading whitespace.
    /// </summary>
    public static bool IsQuiet(string text) => text.TrimStart().StartsWith(QuietMarker, StringComparison.OrdinalIgnoreCase);

    private static string FormatTime(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
