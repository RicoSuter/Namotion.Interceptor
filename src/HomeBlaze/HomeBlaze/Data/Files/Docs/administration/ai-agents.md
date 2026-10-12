---
title: AI Agents
navTitle: AI Agents
position: 6
---

# AI Agents

HomeBlaze has built-in AI agents: subjects that watch parts of the graph, run in the background on a schedule or when values change, and answer questions in a chat. This page is the operator guide. For the design, see [AI Integration](../architecture/design/ai.md). External AI clients that connect to the `/mcp` endpoint are configured separately, see [Configuration](configuration.md) (`McpServer`).

---

## Quick start

1. Install Claude Code for the user HomeBlaze runs as and run `claude` once as that user to log in, see [Setting up the Claude CLI](#setting-up-the-claude-cli).
2. Create a `ClaudeCliAgent` in the subject browser. The defaults only allow reading tools and start no background runs.
3. Open the agent's page and ask it something in the chat.
4. Add `WatchPaths` or a `PollInterval` for background runs, and allow `invoke_operation` so the agent can send notifications.

---

## Overview

There are two agent types. Both offer the same features and differ only in how they reach the model:

- **`ClaudeCliAgent`** runs on the Claude subscription of the user HomeBlaze runs as, through the locally installed and logged-in `claude` CLI (Claude Code).
- **`AnthropicAgent`** calls the Anthropic API with an API key.

An agent is a subject like any other: a JSON file in the `Files` storage (for example `Files/Agents/EnergyAdvisor.json`), created and edited in the subject browser. Every agent:

- runs **background runs** when its poll interval elapses, when a watched value changes, or when you press **Run now**,
- answers **chat** messages on its page,
- records both on one **timeline**, so the chat shows what the background runs found,
- keeps its own **memory**, a small set of key-value notes it carries from run to run,
- uses the HomeBlaze tools to read the graph. By default it can only read: changing devices or sending notifications must be allowed per agent (see [Tools and permissions](#tools-and-permissions)).

---

## Choosing a backend

| | `ClaudeCliAgent` | `AnthropicAgent` |
|---|---|---|
| Account | The Claude subscription of the user HomeBlaze runs as | An Anthropic API key |
| Cost | Counts against the subscription's usage limits | Billed per token |
| Requirements | The `claude` CLI installed and logged in as the HomeBlaze user | Network access to the Anthropic API |
| Where it runs | Bare metal or a VM where you can install and log in to the CLI. The container image does not include the CLI | Anywhere, including the container image |
| Credentials | The CLI keeps its own login; HomeBlaze never stores it | The API key is stored in the agent's JSON file |
| Model | Optional, the CLI default otherwise; aliases such as `opus`, `sonnet` or `haiku` work | Required, a model id such as `claude-sonnet-5` |

The `AnthropicAgent` sends its requests to the URL in the environment variable `ANTHROPIC_BASE_URL` when it is set, for example a proxy, and the API key goes there with them.

---

## Setting up the Claude CLI

1. Install Claude Code for the user that runs HomeBlaze, for example the service account of a systemd unit.
2. Run `claude` once as that user and log in with your Claude account. The login stays with the CLI.
3. Make sure HomeBlaze finds the executable: `CliPath` defaults to `claude`, which is resolved through the `PATH` of the HomeBlaze process. The native installer puts `claude` in `~/.local/bin`, which the `PATH` of a systemd service usually lacks; set `CliPath` to the full path in that case.
4. Keep an HTTP binding for the HomeBlaze web server, for example through `ASPNETCORE_HTTP_PORTS` or an `http://` address in `ASPNETCORE_URLS`. The CLI calls back into HomeBlaze for the tools, see below.

For each turn, HomeBlaze starts `claude -p` in the agent's own empty working directory and hands it a system prompt, the message and an MCP configuration that points to the agent route `/mcp/agents` of the local web server with a token that is valid for this turn only. The CLI runs without its built-in tools (no shell or files, and no web access unless web tools are allowed, see [Web tools](#web-tools)), without the user's own MCP servers, settings, hooks or plugins, without any `CLAUDE.md` of the working directory or the folders above it, and reaches only the tools the agent is allowed to use. HomeBlaze picks the address from the bindings of the web server, preferring HTTP and a loopback or wildcard address, and rewrites a wildcard or `localhost` host to `127.0.0.1`. The route accepts only connections from the same host that carry the token of a running turn, and it is excluded from the HTTPS redirection. The agent route is separate from the external MCP server at `/mcp`: it works when `/mcp` is switched off, and an agent's token never reaches the tools of another agent or of `/mcp`.

HomeBlaze removes the variables that would make the CLI bill the API or another provider (`ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_BASE_URL`, `CLAUDE_CODE_USE_BEDROCK`, `CLAUDE_CODE_USE_VERTEX`, `CLAUDE_CODE_USE_FOUNDRY`, `ANTHROPIC_PROFILE`, `ANTHROPIC_FEDERATION_RULE_ID`, `ANTHROPIC_ORGANIZATION_ID`) from the CLI's environment, so it always runs on the subscription login. Other variables of the HomeBlaze process apply to the CLI as well. For a service account without a browser, `claude setup-token` creates a subscription token that the service can pass in `CLAUDE_CODE_OAUTH_TOKEN`.

---

## Creating an agent

Create a subject of type `ClaudeCliAgent` or `AnthropicAgent` (category AI) in the subject browser, see [Subjects](subjects.md). The edit form groups the settings into models, triggers and limits, allowed tools and the backend settings. The same agents as JSON files:

```json
{
  "$type": "HomeBlaze.AI.Agents.Cli.ClaudeCliAgent",
  "name": "Energy Advisor",
  "instructions": "You watch the PV system, battery and wallbox. Send me a notification when the battery stays below 10 % for more than an hour.",
  "model": "opus",
  "backgroundModel": "haiku",
  "backgroundEffort": "Low",
  "watchPaths": ["/Energy"],
  "pollInterval": "00:15:00",
  "allowedTools": ["browse", "search", "get_property", "list_types", "list_methods", "invoke_query", "get_property_history", "invoke_operation"]
}
```

```json
{
  "$type": "HomeBlaze.AI.Agents.Anthropic.AnthropicAgent",
  "name": "Temperature Monitor",
  "instructions": "You monitor the temperature sensors and tell me when a reading exceeds 80 °C.",
  "model": "claude-sonnet-5",
  "apiKey": "sk-ant-...",
  "watchPaths": ["/Sensors"],
  "minRunInterval": "00:05:00"
}
```

Changes to the configuration apply to the next turn; a running turn finishes with the settings it started with.

---

## Settings

All settings are `[Configuration]` properties, stored in the agent's JSON file. Time spans use `[d.]hh:mm:ss`.

| Setting | Default | Meaning |
|---|---|---|
| `Name` | none | The title of the agent; the type name when empty |
| `Instructions` | none | What the agent watches for and how it reacts. Part of every turn's system prompt |
| `Model` | CLI: the CLI default. API: required | The model of chat turns, and of background runs unless `BackgroundModel` is set |
| `Effort` | model default | How much the model thinks: `Low`, `Medium`, `High`, `ExtraHigh` or `Max` |
| `BackgroundModel` | same as `Model` | The model of background runs |
| `BackgroundEffort` | same as `Effort` | The effort of background runs |
| `WatchPaths` | none | Paths whose changes start a background run and whose current state is included in it |
| `PollInterval` | none | Starts a background run at this interval; at most 365 days |
| `MinRunInterval` | `00:01:00` | The minimum time between the starts of two automatic background runs; at most 365 days |
| `AllowedTools` | the read-only tools | The tools the agent may use, see [Tools and permissions](#tools-and-permissions). An empty list leaves only `update_memory` |
| `TurnTimeout` | `00:05:00` | The time one turn may take; `00:00:00` for no limit, at most about 24 days |
| `MaxToolCallsPerTurn` | `25` | The tool calls of one turn; further calls return an error and the model answers with what it has. `update_memory` does not count |
| `CliPath` | `claude` | `ClaudeCliAgent` only. The CLI executable; a bare name is resolved through `PATH` |
| `FallbackModel` | none | `ClaudeCliAgent` only. The model the CLI switches to when the main model is overloaded |
| `ApiKey` | none, required | `AnthropicAgent` only. A secret: hidden in the property panel and masked in the edit form, where an empty field keeps the stored key |
| `MaxTokens` | `16000` | `AnthropicAgent` only. The output token limit of each model request, thinking included. The edit form requires at least 256 |
| `HistoryTokenBudget` | `100000` | `AnthropicAgent` only. When a chat request uses more input tokens, older chat messages are summarized; `0` never summarizes |

The effort is passed to the CLI as `--effort` and to the API as `output_config.effort`. On the API, any effort turns on adaptive thinking, whose tokens count against `MaxTokens`; without an effort the model's default applies.

The agent also has state properties that show in its property panel and that MCP clients can read: `Status`, `StatusMessage`, `IsBusy`, `Last Analysis` (the newest background result that was not quiet), `Last Activity` (the start of the newest chat turn or background run, also shown on the Activity tab), `Last Background Run`, `Background Runs` and, for CLI agents, the subscription usage of the last five hours and seven days.

---

## Triggers

A background run starts when:

- the **poll interval** elapses,
- a **watched value changes**: any change of a `[State]` property at or below one of the watch paths, matched by whole path segments. `/` watches the whole graph. Changes are collected for 100 milliseconds before they request a run, and properties of agents never trigger a run, so two agents cannot keep starting each other,
- you press **Run now** on the agent page.

Runs never overlap, also not with chat turns. Requests that arrive while a run is busy or waiting are combined into one follow-up run. Automatic runs, from the poll interval or a change, wait until `MinRunInterval` has passed since the start of the previous run, so a value that keeps changing starts at most one run per interval. **Run now** ignores the interval, backoff and pauses.

When a run fails, automatic runs wait 1, 2, 4 and up to 30 minutes after consecutive failures; the next successful run or chat turn resets this. Two kinds of failure pause automatic runs instead, and the status shows the agent as unavailable with the reason:

- **Configuration errors**, such as a CLI that is not logged in or a rejected API key, pause until the configuration is saved (also unchanged), **Run now** succeeds or a chat turn succeeds.
- **Usage limits** pause until the reported reset time, or with the backoff above when none is reported; the agent then shows as idle again without waiting for a run.

An invalid interval, such as a poll interval above 365 days, sets the status to an error and stops automatic runs until it is corrected.

---

## Background runs and chat

Background runs and chat turns share the instructions, the memory and the timeline, but not the conversation:

- A **background run** starts without any chat history. Its message holds the trigger, the five newest background results that were not quiet, the time of the last quiet run, and the current state of every watch path, one JSON object per path in a block marked as data: a subject browsed two levels deep with property values, a property with its value, or a note that nothing was found. The agent answers with a short finding; when nothing is noteworthy, it answers with one line that starts with `[quiet]`.
- The **chat** is one ongoing conversation per agent. Each chat message is sent together with the background findings that are not quiet and arrived since the agent's last reply, so the agent knows what its background runs reported. **New conversation** asks for confirmation, then discards the conversation and adds a divider to the timeline; memory and timeline stay.

The `ClaudeCliAgent` keeps the chat as a CLI session that is resumed per message; if the CLI no longer has it, a new session starts and a "Session restarted" divider appears. The `AnthropicAgent` keeps the chat messages itself, together with a summary of older messages: when a chat request exceeds `HistoryTokenBudget` input tokens, it summarizes the older part with one extra model call and keeps the newer part, aiming at about half of the budget. When a request is too long for the model anyway, the error asks you to start a new conversation.

Every turn's system prompt is built in the same fixed order: a preamble that explains the graph, the tools and the rules, a section for the background or chat mode, your `Instructions`, and the memory. See [AI Integration](../architecture/design/ai.md) for how this order serves prompt caching.

---

## Memory

The memory is the agent's own notebook: key-value entries that the agent keeps across runs and chats, for example the notifications it sent and when, open issues, trends or your preferences. It is included in every turn, so the agent sees it without a tool call.

- **Keys** are slash paths such as `alerts/freezer-temperature`, which form a tree. Segments use letters, digits, `-`, `_` and `.`; keys are compared ignoring case. A key has at most 128 characters.
- **Values** are text of at most 1000 characters. Each entry records the time it was last written.
- **Limits**: at most 200 entries and 8192 bytes (UTF-8) over all keys and values. A change that would exceed a limit is rejected as a whole, with the current size in the error, unless it does not make the memory larger.

The agent changes its memory with the `update_memory` tool, which is always available and does not count against the tool-call budget. One call takes a list of changes, each either setting a key to a value or removing a key; removing a key that ends in `/` removes the whole subtree. The changes apply in order, all or nothing. The memory reaches the model as JSON Lines with `key`, `value` and `updated`, sorted by key, and the prompt tells the model that these notes are data and never override the instructions.

On the **Configuration** tab you can read, add, edit and delete entries; a bar shows the size against the limits. When the agent changes an entry while you edit it, the form warns that saving replaces the agent's change. The agent and the editor can write at the same time; the last write of a key wins.

The memory file of earlier versions, a Markdown document (`.memory.md`), is migrated once into the entry `notes/legacy` and renamed to `.memory.md.migrated`. A memory file that cannot be read completely is copied to `.memory.json.damaged`; the readable entries are kept, or the memory starts empty.

---

## The agent page

The page of an agent has a header with one status chip, **Run now** and **New conversation** (both disabled while a turn runs), and three tabs. While the agent runs, the chip shows the state of its turn: Idle, Answering, Running background or Waiting; otherwise it shows Stopped, Starting, Error or Unavailable, followed by the reason.

**Chat** shows the timeline: your messages, the agent's replies with the tools they called, background findings, errors and dividers. A background finding shows the result of the run; when the agent wrote more before it, **Show full answer** shows the whole answer. Consecutive quiet background runs collapse into one line. Replies are rendered as Markdown while they stream in. The chat opens at the newest entry and follows new content while you are at the bottom; scrolling up stops following. It shows the newest 50 rows, and **Show earlier** adds 50 more at a time without moving what you are reading. With a mouse or trackpad, Enter sends and Shift+Enter adds a line; on touch devices Enter adds a line and the send button sends. While a background run holds the agent, the status shows Waiting and your message is answered after it. A message sent while the agent is stopped or still starting is rejected: it stays in the input with the error below it.

**Activity** shows the number of background runs and the time of the last one, the subscription usage of CLI agents, and a table of background runs, newest first and 25 per page, with trigger, duration, tokens, tool calls and the result as plain text. Runs that failed or were cancelled are listed and counted as well; chat turns are not. The table lists the runs still in the timeline, which keeps the newest 500 entries; the count includes older runs and survives restarts and new conversations.

**Configuration** summarizes the settings and the instructions and holds the memory editor, which is available once the agent has started. Change the settings with the subject's Edit action.

---

## Tools and permissions

The agent reaches the graph through the HomeBlaze MCP tools. Each turn offers exactly the tools in `AllowedTools`, plus `update_memory`. This is enforced where the tools are served, in-process for the `AnthropicAgent` and on the per-turn agent route for the `ClaudeCliAgent`, so it does not depend on what the model is told.

| Tool | Default | What it does |
|---|---|---|
| `browse` | on | Browses the subject tree and reads property values |
| `search` | on | Finds subjects by title, path or type |
| `get_property` | on | Reads one property value |
| `list_types` | on | Lists interfaces and concrete types, so subjects can be found by capability |
| `list_methods` | on | Lists the methods of a subject, each with its kind (`query` or `operation`) and whether it requires confirmation |
| `invoke_query` | on | Calls a query method, which only reads |
| `get_property_history` | on | Reads the recorded history of a property |
| `invoke_operation` | off | Calls an operation method, which can change devices or send something |
| `set_property` | off | Writes a property value |
| `edit_markdown` | off | Replaces one passage of a Markdown page or document, see [Changing pages](#changing-pages) |
| `write_markdown` | off | Replaces the whole text of a Markdown page or document |
| `create_markdown` | off | Creates a Markdown page or document in a folder |
| `update_memory` | always | Changes the agent's memory |
| `web_search` | off | Searches the web, see [Web tools](#web-tools) |
| `web_fetch` | off | Loads a web page, see [Web tools](#web-tools) |

Only methods marked `[Query]` or `[Operation]` are visible to the tools, and `invoke_query` calls only queries while `invoke_operation` calls only operations. A string result longer than 64K characters is cut and ends with a `[truncated: N characters]` line.

**Reading pages and documents.** Markdown pages and documents, such as these docs under `/Docs`, are the main source of context about an installation. The agent reads them with `invoke_query` and the query `ReadEvaluatedContent` of the page, which returns the text with every `{{ path }}` expression replaced by its current value. With `includeSourcePaths` set to true, each value is followed by the absolute path of its property, for example `21.5 (/Devices/Kitchen/Temperature)`, which the agent can read with `get_property` when the property is state. `ReadRawContent` returns the source with the expressions unevaluated. Both queries show the secret configuration values in the arguments and `json` blocks of the blocks as `***`. For a block that creates no subject, for example because of an error or because it stands inside a block that is an error, they hide every argument value and its `json` block, for a head line that cannot be read everything after its name, in a `json` block every value that does not match the subject, and for the old `subject(name)` form the whole content of the fence. A page expression never shows a secret configuration value, nor the raw source of a page through its `Content` property. The tool descriptions and the preamble point the agent to these pages, and the external MCP server sends the same hint to its clients as server instructions.

**Changing things.** With `invoke_operation` or `set_property` allowed, the edit form marks the agent as able to change devices and send notifications. The preamble tells the agent to change something only when you asked for it in the chat or its instructions call for it, and to say in its reply what it changed. Some limits apply regardless:

- Agents cannot change **configuration**: `set_property` only reaches `[State]` properties, and it refuses a property that is also marked `[Configuration]` (for example `IsEnabled` of an OPC UA server or client, or the mode of a GPIO pin) with the error "'path' is a configuration property; agents cannot change configuration.". The settings of agents themselves are not reachable at all.
- Agents cannot run **operations of agents**, such as Run now or New conversation of another agent: "Agents cannot run operations of agents."
- **Operations that require confirmation** cannot be called through MCP at all, neither by agents nor by external MCP clients; run them from the UI, which asks you to confirm. Among them are reboot and firmware update of a Wallbox, its Unlock, Set ICP Max Current and the charge port unlock and toggle operations, unlocking and toggling a door lock, adding and removing plugins, starting and stopping the OPC UA server and client, and New conversation of an agent. `list_methods` reports these with `requiresConfirmation`.

Two gaps remain: an agent allowed to call operations can still release a Wallbox's charge port through **Pause Charging**, see [Wallbox](../devices/Wallbox.md#operations). And the configuration rule applies to agents only: an external MCP client with write access (`McpServer:ReadOnly` set to false) can still set `IsEnabled` of the OPC UA server and client or the mode of a GPIO pin.

**Notifications.** To notify you, an agent with `invoke_operation` finds the `INotificationPublisher` interface with `list_types`, the subjects that implement it with `search`, and calls their `SendNotification` operation. See [Notifications](../development/notifications.md). Without `invoke_operation` the agent can only report findings on its page.

### Changing pages

Markdown files in local storage (on disk or in memory) have three operations: `ReplaceText` replaces one exact passage that occurs once in the source, `ReplaceContent` replaces the whole source, and `CreateMarkdownFile` of a storage or folder creates a new file with a plain name ending in `.md`. The tools `edit_markdown`, `write_markdown` and `create_markdown` call exactly these operations and can each be allowed on their own; the edit form marks them with "Changes pages and documents". `invoke_operation` reaches the operations too, so an agent with it can change pages even with the three tools turned off.

The operations change text only. They keep every [block](pages.md#blocks) exactly as it is and in the same order, and they add none of this active content: raw HTML, images and media, math and diagram blocks, element attributes such as `{onclick=...}`, abbreviation definitions such as `*[HB]: HomeBlaze`, which change the text inside blocks and expressions, and links other than http, https and mailto. Active content a page already has may stay or be removed; change it in the page editor.

- **Blocks**: A block starts with a head line of two or more colons, such as `:: HomeBlaze.Samples.Motor#motor TargetSpeed=1000`, and ends with its closing line. Widget blocks, slot blocks such as the `Footer` of a card, an `Item` of a flow layout or a `Tab` of a tab layout, and style blocks such as `:: {.note}` all count, with their arguments, their `json` block and everything inside them, also their text. A block creates or configures running subjects of any type, so an agent cannot add, change, remove or move one.
- **Text after a block**: Text directly after a block without a closing line belongs to that block, so it cannot be added there. Text before a block, between blocks with closing lines and after the last closing line can be changed.
- **Blocks without a name**: The page knows a block without a `#name` by its type and its position among the pieces of the content around it: blocks, expressions and the stretches of text between them. A change that adds or removes an expression, the first text, or text between two blocks before such a block, at the same level (the page, or the content of the block around it), would create its subject again, so the operations refuse it. A block with a `#name` keeps its subject wherever it is, and the blocks inside it count their position from its content, so text before a named block can change. Editing text that is already there is allowed, and an operator can give the block a `#name` in the page editor. See [What an Edit of the Page Keeps](pages.md#what-an-edit-of-the-page-keeps).
- **Secret values**: The operations work on the source as `ReadRawContent` shows it. A block that holds a hidden secret value, and a code block of the old `subject(name)` form, which counts like a block up to its closing fence or the end of the page, is kept by repeating it with `***` as shown; the page keeps the real value. A passage for `ReplaceText` that overlaps a hidden value is not found, and a block with a guessed value is refused like any other changed block, so neither tells anything about the value. `write_markdown` can therefore rewrite a page with secrets, but `edit_markdown` on the text outside the blocks is the safer choice.

The operations accept at most 64 KB of source, measured as the agent writes it with the hidden values as `***`, so the limit tells nothing about them either; the file can be larger by the hidden values. They write through the same path as the page editor, so an open page updates as after a save. JSON and other files cannot be changed this way.

One small gap remains: an agent can add a link reference definition with an http or https address, which changes where a `[text][label]` link inside a block leads.

The `Docs` folder of the data directory holds the documentation of the running version and is replaced on every start where a seed directory is configured, as in the container image. The operations therefore refuse to change a page there or to create one there, with the error "Pages under the documentation folder are replaced on start and cannot be changed by agents.", also through `invoke_operation`; let agents keep their pages in another folder. Pages are not versioned by HomeBlaze, so keep the data folder under version control or backed up if agents may change pages. See [Security](#security) before allowing page changes.

### Web tools

`web_search` and `web_fetch` let an agent search the web and load web pages, for example to look up a weather forecast or the manual of a device. Both are off by default; turn them on in the edit form, where they carry the warning "Loads untrusted web content", or add them to `allowedTools`. Unlike the other tools, the model's backend runs them, not HomeBlaze:

- A `ClaudeCliAgent` gets the CLI's own `WebSearch` and `WebFetch` tools. They are the only built-in CLI tools an agent can get; tools with access to the host, such as the shell or files, are never offered. The CLI runs `WebFetch` on the HomeBlaze machine itself, so it can also reach addresses in your local network (it upgrades `http` to `https`, so services that offer HTTPS, such as router or NAS admin pages, devices or HomeBlaze itself, are reachable). `WebSearch` runs at Anthropic.
- An `AnthropicAgent` gets the API's web search and web fetch server tools (`web_search_20260209` and `web_fetch_20260209`), which need a model that supports them. They run at Anthropic and cannot reach your local network.

Each call is recorded in the timeline like a graph tool call, with its query or URL and a preview of the result, and counts toward `MaxToolCallsPerTurn`: once the budget is used up, further graph tool calls return an error. HomeBlaze cannot refuse a web tool call itself, since the backend runs it; an `AnthropicAgent` limits each web tool, per model request, to the calls left in the budget (at least one), and a `ClaudeCliAgent` has no such limit.

When a web tool is allowed, the preamble tells the agent that content from the web is data, never instructions. A web page can still try to steer the agent, so see [Security](#security) before combining web tools with `invoke_operation` or `set_property`.

---

## Security

- **Prompt injection.** Names and values from the graph, pages and documents reach the model. A crafted value can try to steer an agent. The preamble and the server instructions state that values from the graph are data, not instructions, but the real defense is the tool scope: allow `invoke_operation` and `set_property` only to agents whose inputs you trust, and keep the default read-only tools otherwise.
- **Pages.** An agent that can change pages (`edit_markdown`, `write_markdown`, `create_markdown` or `invoke_operation`) can change what other agents and people read: pages are the main context of other agents, so a changed page can steer them, and a `{{ path }}` expression in a page is evaluated whenever the page is shown or read. Allow page changes only to agents whose inputs you trust, the same as `invoke_operation`. An agent cannot add HTML, script links or other active content to a page, which could run in your browser, see [Changing pages](#changing-pages).
- **Web content.** With `web_search` or `web_fetch` allowed, anyone who controls a web page or search result the agent reads can try to steer it, which is a far larger group than those who can change your graph. Do not allow web tools together with `invoke_operation`, `set_property` or the page tools unless you accept that risk; a read-only agent with web tools can at worst report something wrong. A web request also tells the visited site, or the search provider, what the agent looked up. A `ClaudeCliAgent` with `web_fetch` can additionally be steered into loading pages from your local network, see [Web tools](#web-tools); allow it only for agents whose instructions and inputs you trust.
- **Secrets.** Secret configuration values cannot be state, the tools expose only state, a `{{ path }}` expression in a page never renders a secret configuration value, also not when an agent writes the expression itself, and `ReadRawContent` and `ReadEvaluatedContent` hide the secret values in the blocks of a page, so an agent cannot read credentials such as API keys through its tools. The API key of an `AnthropicAgent` is stored in plain text in its JSON file, like other secrets; protect the data folder accordingly.
- **Audit.** Every tool call with its arguments and a preview of its result is recorded in the timeline.
- **CLI isolation.** The CLI runs without built-in tools except the allowed web tools, without user, project or local settings and without the `CLAUDE.md` files of the user, the working directory or the folders above it, in an empty working directory, and reaches HomeBlaze only through the agent route with a per-turn token. The MCP configuration file with the token is created readable only by the HomeBlaze user on Linux and macOS, and is deleted after the turn, when the token is revoked.

---

## Usage and cost

Background runs usually dominate the usage, because they run without anyone asking. To keep them cheap:

- set a smaller `BackgroundModel` (for example `haiku` for the CLI) and a low `BackgroundEffort`,
- watch only the paths the agent needs, and raise `MinRunInterval` for values that change often,
- prefer a longer `PollInterval`, or none when change triggers are enough.

Web tools add to the cost: on the API, each web search is billed per search on top of the tokens, and fetched pages and search results count as input tokens; on the subscription they count toward the usage limits.

The Activity tab shows the tokens of each run. For CLI agents it also shows the subscription utilization of the last five hours and seven days as the CLI reports it; when a limit is reached, the agent pauses until the reported reset time.

---

## Files

Besides its configuration file, each agent keeps its data in the `Agents` folder of the [data folder](configuration.md#data-folder), named after the agent's path with slashes replaced by underscores (`/Agents/EnergyAdvisor` becomes `Agents_EnergyAdvisor`). When the path itself contains an underscore or a character that file names cannot hold, the name ends with `__` and a short hash of the path, so no two agents share files (`/Agents/Energy_Advisor` becomes `Agents_Energy_Advisor__` followed by eight hex digits); files of such an agent under the earlier name without the hash are renamed on start, each one only when no file of the new name exists, and not at all when the earlier name is the name of another subject's path, whose files they are then. Paths that differ only in letter case get names that differ only in letter case, which share files on a file system that ignores case, such as the Windows default; give such agents names that differ in more than case:

| File | Content |
|---|---|
| `<name>.timeline.jsonl` | The timeline as JSON Lines. The newest 500 entries are kept; tool results are kept as previews of up to 2000 characters |
| `<name>.memory.json` | The memory entries |
| `<name>.session.json` | The chat session: the CLI session id, or the messages and summary of an `AnthropicAgent` |
| `<name>.stats.json` | The number of background runs and the time of the last activity |
| `<name>.workdir/` | The working directory of the CLI |

Renaming or moving the agent's file changes its path and so starts with new, empty files; the old files stay until you delete them. Deleting the files of an agent resets its timeline, memory, chat and run count.

---

## Troubleshooting

| Message | Cause and remedy |
|---|---|
| The Claude CLI is not logged in. Run claude once as the HomeBlaze user and log in. | Log in as described in [Setting up the Claude CLI](#setting-up-the-claude-cli), then press **Run now** or save the configuration |
| The Claude CLI '...' could not be started | The executable was not found. Install Claude Code for the HomeBlaze user or set `CliPath` to its full path |
| The Claude usage limit is reached until ... | The subscription limit is used up; automatic runs resume after the reset time. Use a smaller background model or fewer runs |
| The address of the web server is not known yet. | The web server has not started yet or listens on no IP address. Keep an HTTP binding; the CLI agent needs it to call back |
| The turn did not finish within ... | Raise `TurnTimeout`, or narrow the instructions and watch paths |
| The tool call budget of N calls for this turn is exhausted. | Shown on a tool call of the agent. Raise `MaxToolCallsPerTurn` if the agent regularly needs more |
| Set the API key of the agent. / The Anthropic API rejected the API key. | Enter a valid key in the edit form |
| Set the model of the agent. | The `AnthropicAgent` needs a model id such as `claude-sonnet-5` |
| The Anthropic API rate limit is reached: ... | A usage limit of the API key; automatic runs pause with the backoff described in [Triggers](#triggers) |
| The Anthropic API did not find the model | Use a model id the API knows, such as `claude-sonnet-5` |
| The model reached the output token limit of N tokens before answering. | Raise `MaxTokens` or lower the effort |
| The request is too long for the model | Start a new conversation, or lower `HistoryTokenBudget` |
| The agent is not running. | A chat message was sent to an agent that is stopped or still starting. Check the status and the log for why it stopped |
| Invalid configuration: ... | An interval is out of range, for example a poll interval above 365 days. Correct it in the edit form |
