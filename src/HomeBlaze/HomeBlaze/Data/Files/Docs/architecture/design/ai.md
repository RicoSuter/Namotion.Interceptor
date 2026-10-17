---
title: AI Integration
navTitle: AI
status: Implemented
---

# AI Integration Design

## Overview [Implemented]

HomeBlaze treats AI as a first-class concern with two complementary modes: **built-in agents** running in-process as subjects, and **external agents** connecting via MCP (Model Context Protocol). Both use the same tool implementations. Operator setup and behavior are in [AI Agents](../../administration/ai-agents.md); this page covers the design.

## Architecture

### Two Agent Modes

**Built-in agents** are `[InterceptorSubject]` classes inside the HomeBlaze process, visible in the knowledge graph with their state, configuration and activity. `ClaudeCliAgent` runs on the Claude subscription through the local `claude` CLI, `AnthropicAgent` on the Anthropic API with an API key.

**External agents** connect via the MCP server with the standard protocol and see the same tools: Claude, ChatGPT, custom copilots or any MCP client.

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
| `HomeBlaze.AI` | `list_methods`, `invoke_query`, `invoke_operation`, `search_markdown`, `edit_markdown`, `write_markdown`, `create_markdown` (via `IMcpToolProvider`) + subject enrichment (`$type`, `$icon`, `$title` via `IMcpSubjectEnricher`) + concrete type discovery (via `IMcpTypeProvider`) | HomeBlaze-specific features |
| `HomeBlaze.History.Mcp` | `get_property_history`, and the planned `get_event_history` and `get_command_history` (see [Messages](messages.md)) | Ships with the history packages |

- **One configuration**: `HomeBlazeMcpConfigurationFactory` builds the configuration for both the MCP server and the built-in agents. Satellite packages contribute tool providers once with `AddHomeBlazeMcpToolProvider`.
- **No dependency on `/mcp`**: the agents get their tools through `HomeBlazeAgentToolSource` and serve them in-process or on their own listener. `McpServer:Enabled` only decides whether the MCP server is registered and `/mcp` is mapped.
- **Transport-agnostic tools**: each tool is a `McpToolInfo` (metadata plus a plain function). It is served as an MCP tool through `McpToolDispatcher` (external agents and the CLI agent route) or wrapped with `ToAIFunction` (the API agent).
- **Why method tools are HomeBlaze-specific**: method discovery uses `MethodMetadata` with `[Operation]` and `[Query]` registry attributes, a HomeBlaze convention, not a core interceptor concept. This keeps the interceptor library independently usable. Property metadata such as units and position is already in the registry as `StateMetadata` and is included in `browse` and `search` with `includeAttributes=true`.
- **Paths** use the slash separator with `[InlinePaths]` flattening, such as `/Servers/OpcUaServer/Port`. See [MCP Server docs](../../../../../../../../docs/mcp.md) for the core MCP server design.

#### Method Tools

- `list_methods` lists each operation and query with name, kind (`query` or `operation`, matching the tool that calls it), title, description, return type and whether it requires confirmation.
- Parameters use the `McpMethodParameter` shape of the core library: JSON Schema `type`, `format`, `enum` names and `nullable`. HomeBlaze adds the unit of an `[OperationParameter(Unit = ...)]` as `description`. A `Percent` parameter reads "fraction where 1 = 100% (0.2 = 20%)", since agents pass the fraction and only the operation dialog takes 0 to 100.
- `invoke_query` calls only `[Query]` methods and `invoke_operation` only `[Operation]` methods, so each permission is exactly one tool. A method of the other kind returns an error naming the right tool, and nothing is invoked. A read-only configuration does not offer `invoke_operation`.
- Arguments are passed by name in a `parameters` object and converted with `McpValueConverter`: an enum accepts its name (ignoring case) or a defined number, a `TimeSpan` its `hh:mm:ss` string.
- An unknown parameter, a missing non-nullable one or a value that does not convert returns an error naming the expected parameters, and the method is not invoked. A missing nullable parameter is passed as null.
- An `ArgumentException` of the method returns its message, for example the list of known values. Any other failure returns a generic error and is logged.
- Long string results are cut, see [Tools and permissions](../../administration/ai-agents.md#tools-and-permissions).

#### Markdown Tools

- `edit_markdown`, `write_markdown` and `create_markdown` (`HomeBlazeMarkdownToolProvider`) are thin tools over `ReplaceText` and `ReplaceContent` of a `MarkdownFile` and `CreateMarkdownFile` of a `VirtualFolder` or `FluentStorageContainer`, so page changes can be allowed without every operation.
- Each tool checks only the type of its target ("'path' is not a Markdown file.", "'path' is not a storage folder."), maps its arguments and calls the operation through `McpMethodInvoker`, the path of `invoke_operation`. Argument conversion, the operation filter, the confirmation rule and error handling are therefore the same.
- The operations check their own rules, the [page write rules](#page-write-rules), and refuse a change with an `ArgumentException` whose message reaches the caller. A read-only configuration does not offer the tools.
- `search_markdown` finds text without reading every page. It matches `query` within single lines as plain text, or as a .NET regular expression with `regex: true`, ignoring case unless `ignore_case` is false, optionally only in or under `path`.
- It searches the `ReadRawContent` view, so expressions stay unevaluated and [hidden values](#hidden-values) are `***` before matching. A secret can neither match nor appear in a result.
- The result lists matching pages in path order, at most `max_pages` (default 20, at most 100), each with its title, the number of matching lines and the line number and snippet of up to five.
- The work is bounded: at most 2,000 pages, 10 seconds per search and 100 ms per regular expression match. `truncated` reports a result cut by a limit, and a regular expression that times out returns an error. Other files, such as JSON, are not searched.

#### Server Instructions and Annotations

- Pages hold most of the documentation and context of an installation. The MCP server's instructions (`HomeBlazeMcpInstructions.ServerInstructions`) tell clients to read them with `invoke_query` and `ReadEvaluatedContent`.
- `browse` and `search` carry the same hint through the `ToolDescriptionAdditions` of the configuration. The `search` hint also states that `Percent` properties hold fractions where 1 is 100%.
- `ReadEvaluatedContent` takes an optional `includeSourcePaths` that follows each evaluated value with the absolute path of its property.
- The agent route `/mcp/agents` sends no server instructions, because the agents' preamble shares that text.
- Annotations: reading tools are `readOnlyHint: true`. `set_property`, `invoke_operation` and the Markdown tools are destructive. Only `invoke_operation` has `openWorldHint: true`, since operations reach systems outside the graph such as notification services. `update_memory` is neither, since it changes only the agent's own memory.
- No tool claims to be idempotent, because writing the same value again can still reach a device. Annotations are hints for clients that decide which calls need approval; enforcement stays with the agent permissions and the confirmation rule.

### Write Limits [Implemented]

- **Configuration**: `set_property` refuses a property marked `[Configuration]`, also when it is `[State]` as well, through the `PropertyWriteFilter` that both the agents and the external `/mcp` server configure. The filter does not reach `invoke_operation`, so an operation can still change a `[Configuration]` property as part of what it does. The agents' own settings are configuration only and so are not reachable at all.
- **Agent operations**: the agents' `invoke_operation` and Markdown tools refuse every operation of an agent subject, so agents cannot start runs or reset conversations of agents.
- **Confirmation**: operations marked `[Operation(RequiresConfirmation = true)]` cannot be invoked through MCP; `invoke_operation` refuses them before reading the arguments. MCP elicitation, the server asking the client's user to confirm during a call, would allow them for clients that support it. It is not implemented, and the CLI agent runs non-interactively and could not answer it.
- **Pages**: the Markdown write operations, which all of these reach, follow the [page write rules](#page-write-rules), because a block becomes a running subject of any type configured outside these filters, HTML runs in the browser of whoever opens the page, and images and media embeds load content from other sites.

The operator view of these limits and their remaining gaps is in [Tools and permissions](../../administration/ai-agents.md#tools-and-permissions).

#### Hidden Values

`ReadEvaluatedContent`, `ReadRawContent` and `search_markdown` hide secrets the same way:

- Secret configuration values in the arguments and `json` blocks of blocks show as `***`.
- A block that creates no subject, for example because of an error or because it stands inside a block that is an error, hides every argument value and its `json` block. A layout block holds no secret and is shown as it is.
- A head line that cannot be read hides everything after its name.
- A `json` block hides every value that does not match the subject.
- The old `subject(name)` form hides the whole content of its fence.
- A page expression never shows a secret configuration value, nor the raw source of a page through its `Content` property, see [Properties Shown on Pages](../../development/building-subjects.md#properties-shown-on-pages).

#### Page Write Rules

`ReplaceText`, `ReplaceContent` and `CreateMarkdownFile` (`MarkdownWriteRules`) change text only:

- **Blocks**: a block runs from a head line of two or more colons, such as `:: HomeBlaze.Samples.Motor#motor TargetSpeed=1000`, to its closing line. Widget, slot (`Footer`, `Item`, `Tab`) and style blocks such as `:: {.note}` stay exactly as they are and in order, with arguments, `json` block and everything inside, text included.
- **Text after a block**: text directly after a block without a closing line belongs to it. Text before a block, between closed blocks and after the last closing line can change.
- **Blocks without a name** are known by type and position among the pieces of the content around them: blocks, expressions and the stretches of text between them. Adding or removing an expression, the first text or text between two blocks before such a block, at the same level, would create its subject again and is refused; editing existing text is allowed. A named block keeps its subject anywhere, and the blocks inside it count from its content. See [What an Edit of the Page Keeps](../../administration/pages.md#what-an-edit-of-the-page-keeps).
- **Active content** is never added: raw HTML, images and media, math and diagram blocks, element attributes such as `{onclick=...}`, abbreviation definitions such as `*[HB]: HomeBlaze` (which change text inside blocks and expressions), and links other than http, https and mailto. Existing active content may stay or be removed. A link reference definition with an http or https address is allowed and can redirect a `[text][label]` link inside a block.
- **Secrets**: the operations work on the `ReadRawContent` view. A block with a hidden value, and an old `subject(name)` code block, which counts like a block up to its closing fence or the end of the page, is kept by repeating it with `***`; the page keeps the real value. A `ReplaceText` passage overlapping a hidden value is not found, and a guessed value is refused like any changed block. `write_markdown` can rewrite a page with secrets, but `edit_markdown` outside the blocks is safer.
- **Size**: at most 64 KB of source with hidden values as `***`, so the limit reveals nothing about them.
- **Write path**: the page editor's, so an open page updates as after a save, see the write lock in [Storage](storage.md). JSON and other files have no such operations.

With `edit_page_layout`, the agent's Markdown tools and `invoke_operation` may also add, change, remove and move layout blocks. The page editor, other callers of the operations and the external MCP server keep the rules above.

- **Layout types** are marked `[PageLayout]`, see [Page Layout Types](../../development/building-subjects.md#page-layout-types). Their block holds only single values of non-secret configuration properties, on the head line or in the `json` block, so it cannot define a subject. The control widgets `Switch`, `Slider`, `Select`, `Control` and `OperationButton` are not layout types.
- **Nested blocks**: `Cell` (`GridLayout`), `Node` (`CanvasLayout`), `Column` (`SubjectTable`), `Series` (`HistoryChart`), `Value` (`ValueList`) and `Case` (`ValueMap`) follow the same rules, so a cell or node may hold a `Widget` or `Card`, never a device.
- **Links and media**: a card's `Href` must be relative or use http, https or mailto. A card with an `Image` is no layout block, because a picture loads content from another site.
- **Subject paths**, such as a `Widget`'s `Path` or the `Map` of a value widget or `Value` row, must lead to an existing subject when their block is new or changed, so a new map and its widget take two edits; on a new page the path must be absolute. An unchanged block may keep a path that leads nowhere.
- **Every other block** stays exactly as `ReadRawContent` shows it, and it and every block around it keep the key by which the page finds their subject: the `#name`, or type and position in the content around it (slot blocks not counted), or slot and position among the unnamed blocks of that slot. No subject is created again or reconfigured. Blocks inside a non-layout block never change. A block of an uninstalled type or in error stays as shown; the layout around it can change.
- **No error boxes**: the page is checked the way it is read, without creating anything. A change that would show a layout block as an error box, such as an invalid value, a second `Footer` or loose text without a place, is refused, because the subjects inside would leave the page. `json` values are checked against their properties.
- **Names**: a layout block may not take another block's `#name`, or one shaped like a key of unnamed content, such as `Card_3`, `Html_2` or `Expression_1`. A page that already repeats a name, or a key in one content, must first be fixed in the page editor, because which block wins follows the page order.
- **Shown values**: value widgets and formatted expressions such as `{{ path | raw }}` show only the [properties shown on pages](../../development/building-subjects.md#properties-shown-on-pages), never a secret.

### Built-in Agents [Implemented]

`LlmAgentBase` is an abstract `[InterceptorSubject]` and `BackgroundService` that owns everything both backends share: configuration, triggers, the turn gate, prompt building, memory, timeline, run statistics and tool scoping. A backend only implements `ExecuteTurnAsync`, which runs one turn, and `ResetSessionAsync`, which discards its chat session. There is no provider subject and no agent framework.

**`AnthropicAgent`** runs the tool loop in-process:

- The Anthropic SDK's `IChatClient` wrapped in `FunctionInvokingChatClient`, with the turn's tools as `AIFunction`s.
- It keeps the chat history itself, persisted as a session file, and summarizes older messages with one extra model call when a chat request exceeds `HistoryTokenBudget` input tokens.
- The summary runs in `OnChatTurnCompletedAsync`, after the answer is recorded and `SendMessageAsync` has returned, still under the turn gate and outside the turn timeout, so the next turn waits for it and reports its usage.
- The web server tools get a constant `max_uses` of `MaxToolCallsPerTurn`, so the tools at the start of the cached prefix stay the same within a turn; their calls still count against the turn's tool-call budget.
- Requests enable automatic prompt caching. The `Max` effort, which `ReasoningOptions` cannot express, is set through the raw request parameters.

**`ClaudeCliAgent`** lets Claude Code run the tool loop:

- It starts `claude -p` per turn with its own system prompt in a file (`--system-prompt-file`), no built-in tools, only the HomeBlaze MCP server, and no user settings, hooks or plugins, in the agent's own working directory, see [Setting up the Claude CLI](../../administration/ai-agents.md#setting-up-the-claude-cli).
- `--bare` is not used because it disables the subscription login.
- Chat turns resume the CLI session; background runs leave no session behind.
- `--max-turns` caps a turn at the same number of model rounds as the API agent, `MaxToolCallsPerTurn` plus 10.
- Subscription utilization from the CLI's rate limit events is shown on the agent page. A limit that rejects a run pauses until that limit's reset, which can be a per-model weekly limit the two utilization windows do not show.

**Agent route.** `AgentMcpListener`, a hosted service, serves the stateless MCP route `/mcp/agents` on a web server of its own on `127.0.0.1`, so the route never shares the bindings, reverse proxy or published ports of the HomeBlaze web server. Each turn registers its tool set under a random bearer token; a request lists and calls exactly that turn's tools, with no server instructions, prompts or resources. Closing the tool set at the end of the turn makes a call that passed the token check just before fail without running. What this exposes is in [How a turn runs the CLI](../../administration/ai-agents.md#how-a-turn-runs-the-cli).

**Tool scoping.** `AllowedTools` is enforced where the tools are served: the API agent receives only the allowed functions, and the agent route lists and calls only the turn's tool set. A per-turn tool-call budget returns an error result once exhausted, so the model answers with what it has; `update_memory` is exempt. Every call is recorded in the timeline with previews of its arguments and result.

**Triggers.** A scheduler starts background runs on the poll interval, on changes of `[State]` properties under the watch paths (buffered briefly, never on properties of agents), and on Run now. Runs never overlap, requests during a run coalesce into one follow-up run, and automatic runs keep `MinRunInterval` between starts. Failures back off, configuration failures pause until the configuration is saved or a turn succeeds, and usage limits pause until the reported reset; the timings are in [Triggers](../../administration/ai-agents.md#triggers).

**Prompt and sessions.**

- The system prompt has a fixed order: preamble, mode section (background or chat), the operator's instructions, then the memory as JSON Lines with `key`, `value` and `updated`, sorted by key. The stable prefix lets the prompt cache reuse it across turns.
- Background runs start without a session. Their message holds the trigger, recent results and the pre-fetched state of each watch path, one JSON object per path in a block marked as data: a subject browsed two levels deep with property values, a property with its value, or a note that nothing was found.
- The chat is one session per agent, and background findings since the last reply are prepended to the next chat message.
- A background reply whose text after the last tool call is a single non-blank line starting with `[quiet]` is recorded but not reported.

**Memory and timeline.** The memory is a set of key-value entries with slash-path keys and update times, changed through `update_memory` in all-or-nothing patches and bounded by entry count and size. The timeline of background runs and chat is kept as JSON Lines with a capped number of entries, see [Files](../../administration/ai-agents.md#files). Both live with the session and the run count in the `Agents` folder of the data directory, outside the browsable storage.

**Agent writes** are local writes through the normal write path (local model -> UNS -> satellite -> device). They need no source tagging, because agents are not connectors: source tagging prevents feedback loops in bidirectional connector sync and is not for attribution. Audit beyond the timeline is a separate concern, see [Audit](audit.md).

### Alternatives Considered

- **Microsoft Agent Framework**: the first plan composed `ChatClientAgent` inside `LlmAgentBase`. It was dropped because `FunctionInvokingChatClient` already runs the tool loop for the API backend, and wrapping the CLI, which runs its own loop, as an `IChatClient` would fight it.
- **Provider subjects**: models were to be resolved through `ILlmProvider` subjects (Anthropic, OpenAI, Ollama) referenced by path. A separate provider only pays off once several agents share credentials, so it moved to the evolution path.
- **Subscription login**: a Claude subscription can only be used through Claude Code, so HomeBlaze runs the user's own installed and logged-in CLI and never handles the login.
- **Notification-only operations**: the plan restricted `invoke_operation` to notification channels; per-agent tool permissions replaced that.

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
