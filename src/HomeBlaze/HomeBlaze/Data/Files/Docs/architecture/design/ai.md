---
title: AI Integration
navTitle: AI
status: Implemented
---

# AI Integration Design

## Overview [Implemented]

HomeBlaze treats AI as a first-class concern with two complementary modes: **built-in agents** running in-process as subjects, and **external agents** connecting via MCP (Model Context Protocol). Both modes access the knowledge graph through the same tool implementations. How operators set up and use the built-in agents is described in [AI Agents](../../administration/ai-agents.md).

## Architecture

### Two Agent Modes

**Built-in agents** are `[InterceptorSubject]` classes that run inside the HomeBlaze process. They are visible in the knowledge graph like any other subject: operators can see their state, configuration and activity. Two backends exist, `ClaudeCliAgent` on the Claude subscription of the user HomeBlaze runs as, through the local `claude` CLI, and `AnthropicAgent` on the Anthropic API with an API key.

**External agents** connect via the MCP server. They use the standard MCP protocol and see the same tools. This is the integration point for Claude, ChatGPT, custom copilots, or any MCP-compatible client.

| Aspect | Built-in Agent | External Agent (MCP) |
|--------|---------------|---------------------|
| Runtime | In-process subject | Remote MCP client |
| Tool access | `AIFunction` calls in-process (`AnthropicAgent`), or the per-turn MCP route `/mcp/agents` (`ClaudeCliAgent`); only the agent's `AllowedTools` | MCP protocol on `/mcp`, read-only unless configured otherwise |
| LLM configuration | On the agent: model and effort per mode, plus the API key or the CLI path | Client-side |
| Visibility | Visible as subject in knowledge graph | Not visible in graph |
| Interaction | Background runs (poll, debounced change under watch paths, Run now) plus a chat | Pull only (push TBD, depends on MCP evolution) |
| Use cases | Reactive automation, continuous monitoring, operator chat | Operator copilot, ad-hoc queries, debugging |

### MCP Tool Layering [Implemented]

MCP tools are split across packages, configured via a single `McpServerConfiguration` object:

| Package | Tools | Scope |
|---------|-------|-------|
| `Namotion.Interceptor.Mcp` | `browse`, `search`, `get_property`, `set_property`, `list_types` | Any Namotion.Interceptor application |
| `HomeBlaze.AI` | `list_methods`, `invoke_query`, `invoke_operation`, `edit_markdown`, `write_markdown`, `create_markdown` (via `IMcpToolProvider`) + subject enrichment (`$type`, `$icon`, `$title` via `IMcpSubjectEnricher`) + concrete type discovery (via `IMcpTypeProvider`) | HomeBlaze-specific features |
| `HomeBlaze.History.Mcp` | `get_property_history`, and the planned `get_event_history` and `get_command_history` (see [Messages](messages.md)) | Ships with the history packages |

`HomeBlazeMcpConfigurationFactory` builds the HomeBlaze configuration for the MCP server and for the built-in agents. Satellite packages contribute tool providers once with `AddHomeBlazeMcpToolProvider`, so both get them. The MCP server is always registered because the agents use its tools; `McpServer:Enabled` only decides whether `/mcp` is mapped.

`list_methods` lists each operation and query of a subject with its name, kind (`query` or `operation`, matching the tool that calls it), title, description, return type and whether it requires confirmation. Its parameters use the `McpMethodParameter` shape of the core library: JSON Schema `type`, `format`, `enum` names and `nullable`. HomeBlaze adds the unit of an `[OperationParameter(Unit = ...)]` as the `description`; a `Percent` parameter reads "fraction where 1 = 100% (0.2 = 20%)", since agents pass the fraction and only the operation dialog takes 0 to 100.

`invoke_query` calls only `[Query]` methods and `invoke_operation` only `[Operation]` methods, so each permission is exactly one tool; calling a method of the other kind returns an error naming the right tool, and nothing is invoked. A read-only configuration does not offer `invoke_operation`. Both tools take the arguments by parameter name in a `parameters` object and convert them with `McpValueConverter`, so an enum accepts its name (ignoring case) or a defined number, and a `TimeSpan` its `hh:mm:ss` string. An unknown parameter, a missing non-nullable one or a value that does not convert returns an error that names the expected parameters, and the method is not invoked; a missing nullable parameter is passed as null. When the method itself rejects an argument with an `ArgumentException`, its message is returned, for example the list of known values; any other failure returns a generic error and is logged. Long string results are cut, see [Tools and permissions](../../administration/ai-agents.md#tools-and-permissions).

`edit_markdown`, `write_markdown` and `create_markdown` (`HomeBlazeMarkdownToolProvider`) are thin tools over the Markdown write operations `ReplaceText` and `ReplaceContent` of a `MarkdownFile` and `CreateMarkdownFile` of a `VirtualFolder` or `FluentStorageContainer`, so an agent can be allowed to change pages without being allowed every operation. Each checks only the type of its target first ("'path' is not a Markdown file.", "'path' is not a storage folder."), maps its arguments to the operation's parameters and calls the operation through `McpMethodInvoker`, the path `invoke_operation` uses, so argument conversion, the operation filter, the confirmation rule and the error handling are the same. The operations themselves refuse storage that is not local and the documentation folder with an `ArgumentException`, whose message reaches the caller. A read-only configuration does not offer them.

Operations marked `[Operation(RequiresConfirmation = true)]` cannot be invoked through MCP, see [AI Agents](../../administration/ai-agents.md#tools-and-permissions) for the rule and its remaining gaps; `invoke_operation` refuses them before reading the arguments. MCP elicitation, the server asking the client's user to confirm during a call, would be the protocol's way to allow these for clients that support it; it is not implemented, and the CLI agent runs non-interactively and could not answer it.

The tools carry MCP tool annotations: the reading tools are `readOnlyHint: true`, `set_property`, `invoke_operation` and the Markdown tools are destructive, `invoke_operation` is the only tool with `openWorldHint: true`, since operations reach systems outside the graph such as notification services, and the agents' `update_memory` is neither, since it changes only the agent's own memory. No tool claims to be idempotent, because writing the same value again can still reach a device. Annotations are hints for external clients that decide which calls need approval; enforcement stays with the agent permissions and the confirmation rule.

Markdown pages and documents hold most of the documentation and context of an installation, so the HomeBlaze MCP server sends server instructions (`HomeBlazeMcpInstructions.ServerInstructions`) that tell clients to read the relevant ones first with `invoke_query` and `ReadEvaluatedContent`, and `browse` and `search` carry the same hint through the `ToolDescriptionAdditions` of the configuration, and the `search` hint also states that properties with unit `Percent` hold fractions where 1 is 100%. `ReadEvaluatedContent` takes an optional `includeSourcePaths` that follows each evaluated value with the absolute path of its property. The agent route `/mcp/agents` sends no server instructions, because the preamble of the built-in agents shares that text.

The tools of the built-in agents never write configuration: `set_property` refuses a property marked `[Configuration]` with an error, also when it is `[State]` as well, through the `PropertyWriteFilter` of their configuration; the external MCP server keeps writing such properties when it is not read-only. The agents' own settings are configuration only and so not reachable through the MCP tools at all. Their `invoke_operation` and Markdown tools also refuse every operation of an agent subject, so agents cannot start runs or reset conversations of agents. The Markdown write operations, which all of these reach, keep the blocks of a page (widget, slot and style blocks with their whole text) exactly as they are and in the same order, except for the layout blocks an agent with `edit_page_layout` may change (see [Editing page layout](../../administration/ai-agents.md#editing-page-layout)), and add no other active content (`MarkdownWriteRules`, listed in [Changing pages](../../administration/ai-agents.md#changing-pages)), because a block becomes a running subject of any type, configured outside these filters, HTML runs in the browser of whoever opens the page, and images and media embeds load content from other sites.

This keeps the interceptor library independently usable. Method tools are HomeBlaze-specific because method discovery uses `MethodMetadata` with `[Operation]`/`[Query]` registry attributes, a HomeBlaze convention, not a core interceptor concept. Property-level metadata (units, position) is already in the registry as `StateMetadata` attributes, included directly in `browse` and `search` responses when `includeAttributes=true`.

Tool implementations are transport-agnostic `McpToolInfo` instances (metadata + plain function). Consumers serve them as MCP tools (for external agents and the CLI agent route, through `McpToolDispatcher`) or wrap them as `AIFunction` objects (for the API agent, through `ToAIFunction`). One implementation, any delivery mode.

HomeBlaze MCP uses slash (`/`) separator with `[InlinePaths]` flattening (e.g., `/Servers/OpcUaServer/Port`). See [MCP Server docs](../../../../../../../../docs/mcp.md) for the core MCP server design.

### Built-in Agents [Implemented]

`LlmAgentBase` is an abstract `[InterceptorSubject]` and `BackgroundService` that owns everything both backends share: configuration, triggers, the turn gate, prompt building, memory, timeline, run statistics and tool scoping. A backend only implements `ExecuteTurnAsync`, which runs one turn, and `ResetSessionAsync`, which discards its chat session. There is no provider subject and no agent framework.

- **`AnthropicAgent`** runs the tool loop in-process: the Anthropic SDK's `IChatClient` wrapped in `FunctionInvokingChatClient`, with the turn's tools as `AIFunction`s. It keeps the chat history itself, persisted as a session file, and summarizes older messages with one extra model call when a chat request exceeds `HistoryTokenBudget` input tokens. The summary runs in `OnChatTurnCompletedAsync`, after the answer is recorded and `SendMessageAsync` has returned, still under the turn gate and outside the turn timeout, so the next turn waits for it and reports its usage. The web server tools get a constant `max_uses` of `MaxToolCallsPerTurn`, so the tools at the start of the cached prefix stay the same within a turn; their calls still count against the turn's tool-call budget. Requests enable automatic prompt caching, and the `Max` effort, which `ReasoningOptions` cannot express, is set through the raw request parameters.
- **`ClaudeCliAgent`** starts `claude -p` per turn with its own system prompt in a file (`--system-prompt-file`), no built-in tools, only the HomeBlaze MCP server, no user settings, hooks or plugins, in the agent's own working directory, see [Setting up the Claude CLI](../../administration/ai-agents.md#setting-up-the-claude-cli). `--bare` is not used because it disables the subscription login. Chat turns resume the CLI session; background runs leave no session behind. `--max-turns` caps a turn at the same number of model rounds as the API agent, `MaxToolCallsPerTurn` plus 10. Claude Code runs the tool loop and reaches the tools through the agent route below. Subscription utilization from the CLI's rate limit events is shown on the agent page; when a limit rejects a run, the pause lasts until the reset of that limit, which can be a per-model weekly limit the two utilization windows do not show.

**Agent route.** `/mcp/agents` is a second, stateless MCP route on the HomeBlaze web server. Each turn registers its tool set under a random bearer token that is revoked when the turn ends; a request must come from the same host and carry a running turn's token, and then lists and calls exactly that turn's tools, with no server instructions, prompts or resources. The tool set is closed when the turn ends, so a call that passed the token check just before returns an error and runs nothing. How the CLI gets the token is described in [Setting up the Claude CLI](../../administration/ai-agents.md#setting-up-the-claude-cli).

**Tool scoping.** `AllowedTools` is enforced where the tools are served: the API agent receives only the allowed functions, and the agent route lists and calls only the turn's tool set. A per-turn tool-call budget returns an error result once exhausted, so the model answers with what it has; `update_memory` is exempt. Every call is recorded with previews of its arguments and result in the timeline. The tools run read-only unless `set_property`, `invoke_operation` or a Markdown tool is allowed.

**Triggers.** A scheduler starts background runs on the poll interval, on changes of `[State]` properties under the watch paths (buffered briefly, and never on properties of agents), and on Run now. Runs never overlap; requests during a run coalesce into one follow-up run, and automatic runs keep `MinRunInterval` between starts. Failures back off, configuration failures pause until the configuration is saved or a turn succeeds, and usage limits pause until the reported reset; the timings are in [Triggers](../../administration/ai-agents.md#triggers).

**Prompt and sessions.** The system prompt is built in a fixed order: preamble, mode section (background or chat), the operator's instructions, then the memory as JSON Lines. The stable prefix lets the prompt cache reuse it across turns. Background runs start without a session and get the trigger, recent results and the pre-fetched state of the watch paths; the chat is one session per agent, and background findings since the last reply are prepended to the next chat message. A background reply whose text after the last tool call is a single non-blank line starting with `[quiet]` is recorded but not reported.

**Memory and timeline.** The agent-owned memory is a set of key-value entries with slash-path keys and update times, changed through `update_memory` in all-or-nothing patches and bounded by entry count and size. The timeline of background runs and chat is kept as JSON Lines with a capped number of entries, see [Files](../../administration/ai-agents.md#files). Both live with the session and the run count in the `Agents` folder of the data directory, outside the browsable storage.

Agent writes are local writes: they flow through the normal write path (local model -> UNS -> satellite -> device). No source tagging is needed because agents are not connectors; source tagging exists specifically to prevent feedback loops in bidirectional connector sync, not for attribution. Audit of agent actions beyond the timeline is a separate concern (see [Audit](audit.md)).

### Alternatives Considered

The first plan composed the Microsoft Agent Framework (`ChatClientAgent`) inside `LlmAgentBase` and resolved the model through `ILlmProvider` subjects (Anthropic, OpenAI, Ollama) referenced by path. It was dropped: `FunctionInvokingChatClient` already runs the tool loop for the API backend, and the CLI backend runs its own loop, so wrapping it as an `IChatClient` would fight the CLI. A Claude subscription can only be used through Claude Code, so HomeBlaze runs the user's own installed and logged-in CLI and never handles the login. A separate provider subject only pays off once several agents share credentials, so it moved to the evolution path. The plan's restriction of `invoke_operation` to notification channels was replaced by per-agent tool permissions.

## Evolution Path

| Stage | Description |
|-------|-------------|
| Stage 1 [Implemented] | `LlmAgentBase` with `ClaudeCliAgent` and `AnthropicAgent`. Read-only by default, per-agent `AllowedTools`. Poll and debounced change triggers, chat, agent memory |
| Stage 2 | Shared provider subject for credentials, declarative trigger filters (for example "temperature > 80"), agent-to-agent calls with recursion limits |
| Stage 3 | Per-agent authorization scopes on subjects instead of per-tool permissions, and an approval workflow for operations that require confirmation |

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Agent runtime | Two backends on one base class: `FunctionInvokingChatClient` for the API, Claude Code for the CLI | Each backend keeps its natural tool loop; no agent framework is needed |
| Credentials | On the agent: the API key as a secret configuration value, or the login of the local CLI, which HomeBlaze never stores | No provider subject until credentials need to be shared |
| Interaction model | Background runs on poll, debounced change under watch paths and Run now, plus one chat session per agent; pull-only for external agents | Reacts to events without a rule language; background findings reach the chat |
| Permissions | Per-agent `AllowedTools`, enforced where tools are served; configuration and agent operations off-limits to agents; confirmation-required operations off-limits to MCP | CLI flags and prompts alone are not a security boundary |
| Session model | Separate background runs, one chat session, agent-owned memory | Continuity at a predictable cost per run; a perpetual session would resend a growing context on every tick |
| Tool split | Core 5 tools in `Namotion.Interceptor.Mcp`, method tools in `HomeBlaze.AI`, history tool in `HomeBlaze.History.Mcp` | Methods are a HomeBlaze convention, and a tool ships with the feature it exposes |
| Agent writes | Local writes, no source tagging | Source mechanism is for connector feedback-loop prevention, not attribution |

## Open Questions

- MCP push/subscription support for external agents (depends on protocol evolution)
- Agent audit trail beyond the timeline: how to attribute changes to specific agents (see [Audit](audit.md))
- Authorization: which agents and clients can access which subjects (see [Security](security.md))
- Capacity planning: many built-in agents watching many paths means aggregate model cost and rate limits; budgets across agents are not designed yet
