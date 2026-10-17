namespace HomeBlaze.AI.Agents;

/// <summary>
/// A tool the model's backend runs itself instead of HomeBlaze, enabled through its name in
/// <see cref="LlmAgentBase.AllowedTools"/> for agent types that list it in <see cref="LlmAgentBase.SupportedBackendTools"/>.
/// </summary>
/// <param name="Name">The name in <see cref="LlmAgentBase.AllowedTools"/>.</param>
/// <param name="Summary">A short operator-facing description.</param>
/// <param name="LoadsWebContent">Whether the tool brings untrusted content from the web into the turn.</param>
public sealed record AgentBackendTool(string Name, string Summary, bool LoadsWebContent)
{
    /// <summary>Searches the web.</summary>
    public static AgentBackendTool WebSearch { get; } = new("web_search", "Searches the web.", LoadsWebContent: true);

    /// <summary>Loads a web page.</summary>
    public static AgentBackendTool WebFetch { get; } = new("web_fetch", "Loads a web page.", LoadsWebContent: true);

    /// <summary>The web tools, in display order.</summary>
    public static IReadOnlyList<AgentBackendTool> WebTools { get; } = [WebSearch, WebFetch];
}
