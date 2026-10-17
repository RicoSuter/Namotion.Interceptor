---
title: AI Agents
navTitle: AI Agents
position: 10
---

# AI Agents

Built-in AI agents are subjects that watch parts of the graph, run in the background on a schedule or when values change, and answer questions in a chat. This page is the operator guide; the design is in [AI Integration](../architecture/design/ai.md). External AI clients on the `/mcp` endpoint are configured separately, see [Configuration](configuration.md) (`McpServer`).

---

## Quick start

1. Install Claude Code for the user HomeBlaze runs as and run `claude` once as that user to log in, see [Setting up the Claude CLI](#setting-up-the-claude-cli).
2. Create a `ClaudeCliAgent` in the subject browser. The defaults only allow reading tools and start no background runs.
3. Open the agent's page and ask it something in the chat.
4. Add `WatchPaths` or a `PollInterval` for background runs, and allow `invoke_operation` so the agent can send notifications.

---

## Overview

An agent is a JSON file in the `Files` storage, for example `Files/Agents/EnergyAdvisor.json`, created and edited in the subject browser. Every agent:

- runs **background runs** on a poll interval, when a watched value changes, or on **Run now**,
- answers **chat** messages on its page,
- records both on one **timeline**, so the chat shows what the background runs found,
- keeps a **memory** of key-value notes from run to run,
- can only read by default. Changing devices or pages and sending notifications must be allowed per agent, see [Tools and permissions](#tools-and-permissions).

---

## Choosing a backend

Both agent types offer the same features and differ only in how they reach the model.

| | `ClaudeCliAgent` | `AnthropicAgent` |
|---|---|---|
| Account | The Claude subscription of the user HomeBlaze runs as | An Anthropic API key |
| Cost | Counts against the subscription's usage limits | Billed per token |
| Requires | The `claude` CLI (Claude Code) installed and logged in as the HomeBlaze user | Network access to the Anthropic API |
| Runs | Where you can install the CLI; not in the official container image, see [Docker](#docker) | Anywhere, including the container image |
| Credentials | The CLI keeps its own login; HomeBlaze never stores it | The API key, stored in the agent's JSON file |
| Model | Optional, the CLI default otherwise; aliases such as `opus`, `sonnet` or `haiku` work | Required, a model id such as `claude-sonnet-5` |

The `AnthropicAgent` sends its requests, with the API key, to `ANTHROPIC_BASE_URL` when that variable is set, for example to a proxy. Local Anthropic profiles, such as `~/.config/anthropic`, never apply to it.

---

## Setting up the Claude CLI

1. Install Claude Code for the user that runs HomeBlaze, for example the service account of a systemd unit.
2. Run `claude` once as that user and log in. The login stays with the CLI. Without a browser, `claude setup-token` creates a subscription token to pass in `CLAUDE_CODE_OAUTH_TOKEN`.
3. Check `CliPath` (default `claude`, resolved through the `PATH` of the HomeBlaze process):
   - The native installer puts `claude` in `~/.local/bin`, which a systemd service's `PATH` usually lacks. Set the full path.
   - On Windows, use `claude.exe`. A `.cmd` or `.bat` file, such as the `claude.cmd` shim of npm, is refused.

### How a turn runs the CLI

- HomeBlaze starts `claude -p` per turn in the agent's own working directory. The message goes on standard input; the system prompt and MCP configuration are in two temporary files only the HomeBlaze user can read (file permissions on Linux and macOS, an access list on Windows), deleted after the turn.
- The CLI gets no built-in tools (no shell or files, no web unless [web tools](#web-tools) are allowed), none of the user's MCP servers, settings, hooks or plugins, and no `CLAUDE.md` of the working directory or above.
- It reaches only the agent's allowed tools, on the agent route `/mcp/agents`, which HomeBlaze serves on its own listener on `127.0.0.1` with a port the operating system assigns at each start. The web server's bindings, HTTPS settings, published ports and reverse proxy neither expose nor affect it.
- The route accepts only the token of a running turn, also from other local processes. The token is revoked when the turn ends, and a later tool call returns an error and runs nothing. It never reaches the tools of another agent or of `/mcp`, and the route works when `/mcp` is off.

### The CLI environment

The CLI always runs on the subscription login:

- **Removed**: every variable starting with `ANTHROPIC_` or `CLAUDE_CODE_USE_`, and `CLAUDE_CODE_API_BASE_URL`, `CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR`, `CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST`, `CLAUDE_CODE_HOST_AUTH_ENV_VAR` and `CLAUDE_CODE_HOST_CREDS_FILE` (ignoring case on Windows), which would bill the API or another provider or redirect requests. `CLAUDE_CODE_OAUTH_TOKEN` is kept, and other variables apply as usual.
- **No profiles**: `ANTHROPIC_CONFIG_DIR` points to `.anthropic-config` in the working directory, emptied at each run and private to the HomeBlaze user on Linux and macOS. If it cannot be emptied, the turn fails with a configuration error that names it.
- **No proxy for the route**: `NO_PROXY` and `no_proxy` keep their entries and exclude `127.0.0.1`, so a proxy in `HTTP_PROXY` or `HTTPS_PROXY` never sees the route's calls or token.
- **Outside API keys**: when the CLI's first output names an API key source outside the environment, such as a Console login or an `apiKeyHelper` of the managed settings, HomeBlaze stops it before its first model request completes and fails the turn with a configuration error that names the source. If the output names no key source, as a changed CLI version might, HomeBlaze logs a warning for that run.

### Docker

- **Image**: the official image has no Claude CLI. Build an image from it that installs the CLI, with the native installer or with Node.js and `npm install -g @anthropic-ai/claude-code`. The CLI must run in the HomeBlaze container, which starts it per turn over the container's loopback address.
- **Path**: as root, the native installer puts `claude` in `/root/.local/bin`, which is not on the `PATH`. Set `CliPath` to `/root/.local/bin/claude`, add the folder to `PATH`, or link it into `/usr/local/bin`, where npm puts it.
- **Login**: run `claude setup-token` on a machine with a browser and pass the token in `CLAUDE_CODE_OAUTH_TOKEN`. The token is long-lived: keep it out of the compose file and shell history, for example in an `env_file` only its owner can read. Environment values show in `docker inspect`; a Docker secret that your image's entrypoint exports avoids that.
- **Ports**: publish only the web server's port. The agent route is not reachable through published ports or a reverse proxy. With host networking or a shared network namespace, such as Kubernetes sidecars or WSL mirrored mode, other local processes can connect but still need a turn's token.
- **Sessions**: the CLI keeps chat sessions in `~/.claude`. Without a volume there, recreating the container starts a fresh chat session; timeline and memory stay in the data folder. The folder holds chat transcripts with tool results and device data, so keep the volume private.

---

## Creating an agent

Create a `ClaudeCliAgent` or `AnthropicAgent` (category AI) in the subject browser, see [Subjects](subjects.md). Configuration changes apply to the next turn; a running turn keeps the settings it started with. As JSON files:

```json
{
  "$type": "HomeBlaze.AI.Agents.ClaudeCli.ClaudeCliAgent",
  "name": "Energy Advisor",
  "instructions": "You watch the PV system, battery and wallbox. Send me a notification when the battery stays below 10 % for more than an hour.",
  "model": "opus",
  "backgroundModel": "haiku",
  "backgroundEffort": "Low",
  "watchPaths": ["/Energy"],
  "pollInterval": "00:15:00",
  "allowedTools": ["browse", "search", "search_markdown", "get_property", "list_types", "list_methods", "invoke_query", "get_property_history", "invoke_operation"]
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

---

## Settings

All settings are `[Configuration]` properties in the agent's JSON file. Time spans use `[d.]hh:mm:ss`.

| Setting | Default | Meaning |
|---|---|---|
| `Name` | type name | The title of the agent |
| `Instructions` | none | What the installation is, the agent's role, what it watches for and how it reacts. Part of every system prompt |
| `Model` | CLI default; required for API | The model of chat turns, and of background runs unless `BackgroundModel` is set |
| `Effort` | model default | `Low`, `Medium`, `High`, `ExtraHigh` or `Max` |
| `BackgroundModel` | `Model` | The model of background runs |
| `BackgroundEffort` | `Effort` | The effort of background runs |
| `WatchPaths` | none | Paths whose changes start a background run and whose state is included in it |
| `PollInterval` | none | Starts a background run at this interval; at most 365 days |
| `MinRunInterval` | `00:01:00` | Minimum time between the starts of two automatic runs; at most 365 days |
| `AllowedTools` | read-only tools | See [Tools and permissions](#tools-and-permissions). An empty list leaves only `update_memory` |
| `TurnTimeout` | `00:05:00` | Time one turn may take; `00:00:00` for no limit, at most about 24 days |
| `MaxToolCallsPerTurn` | `25` | Tool calls of one turn, see below |
| `CliPath` | `claude` | CLI only. The executable; a bare name is resolved through `PATH` |
| `FallbackModel` | none | CLI only. Used when the main model is overloaded |
| `ApiKey` | required | API only. Secret: hidden in the property panel, masked in the edit form, where an empty field keeps the stored key |
| `MaxTokens` | `16000` | API only. Output token limit per model request, thinking included; at least 256 in the edit form |
| `HistoryTokenBudget` | `100000` | API only. Above this many input tokens per chat request, older messages are summarized; `0` never summarizes |

- **Effort** goes to the CLI as `--effort` and to the API as `output_config.effort`. On the API it turns on adaptive thinking, whose tokens count against `MaxTokens`.
- **Tool calls** beyond `MaxToolCallsPerTurn` return an error and the model answers with what it has. `update_memory` does not count.
- **Model rounds** are capped at `MaxToolCallsPerTurn` plus 10. The last round of an `AnthropicAgent` offers no tools, so the model answers; a `ClaudeCliAgent` turn that runs out of rounds fails.

State properties, in the property panel and readable by MCP clients: `Status`, `StatusMessage`, `IsBusy`, `Last Analysis` (newest background result that was not quiet), `Last Activity` (start of the newest turn), `Last Background Run`, `Background Runs` and, for CLI agents, the subscription usage of the last five hours and seven days.

---

## Triggers

A background run starts when the **poll interval** elapses, when a **watched value changes**, or on **Run now**.

- A watched change is any change of a `[State]` property at or below a watch path, matched by whole path segments. `/` watches the whole graph.
- Changes are collected for 100 milliseconds before they request a run. Properties of agents never trigger one, so agents cannot keep starting each other.
- Runs never overlap, also not with chat turns. Requests during a run combine into one follow-up run.
- Automatic runs wait until `MinRunInterval` has passed since the previous run started, so a value that keeps changing starts at most one run per interval.
- **Run now** ignores the interval, the backoff and the pauses.

### Failures and pauses

| Failure | Automatic runs | Until |
|---|---|---|
| A run fails | Wait 1, 2, 4 and up to 30 minutes after consecutive failures | The next successful run or chat turn |
| Configuration error, such as a CLI that is not logged in or a rejected API key | Pause; status Unavailable with the reason | The configuration is saved (also unchanged), or **Run now** or a chat turn succeeds |
| Usage limit | Pause; status Unavailable with the reason | The limit's reset, or the backoff above without a reported reset time. The agent then shows idle without a run. Saving does not end it |
| Invalid interval, such as a poll interval above 365 days | Stop; status Error | The interval is corrected |

---

## Background runs and chat

Both share the instructions, memory and timeline, but not the conversation.

- **Background runs** start without chat history. They get the trigger with the server time zone, the five newest results that were not quiet, the time of the last quiet run, and the current state of each watch path.
- **Quiet runs**: when nothing is noteworthy, the agent answers with one line starting with `[quiet]`, which is recorded but not reported. Only the text after the last tool call counts.
- **Chat** is one ongoing conversation per agent. Each message carries the findings that are not quiet since the agent's last reply.
- **New conversation** asks for confirmation, then discards the conversation and adds a divider. Memory and timeline stay.
- **`ClaudeCliAgent`** resumes a CLI session per message. If the CLI lost it, a new one starts with a "Session restarted" divider.
- **`AnthropicAgent`** keeps the messages itself. Above `HistoryTokenBudget`, one extra model call after the reply summarizes the older part, aiming at half the budget; the next message waits for it and counts its tokens.
- **System prompt**: a preamble on the graph and the rules, the background or chat section, your `Instructions`, then the memory. The preamble names no kind of installation, so say in `Instructions` what the installation is and what the agent is for.

---

## Memory

The agent's own notebook of key-value entries, kept across runs and chats, such as notifications it sent, open issues, trends or your preferences. It is in every prompt, so the agent needs no tool call to see it.

| | Rule |
|---|---|
| Keys | Slash paths such as `alerts/freezer-temperature`, forming a tree. Letters, digits, `-`, `_` and `.`, compared ignoring case. At most 128 characters |
| Values | Text of at most 1000 characters, with the time it was last written |
| Total | At most 200 entries and 8192 bytes (UTF-8). A change beyond a limit is rejected as a whole, with the current size in the error, unless it does not grow the memory |

- **`update_memory`** is always available and does not count against the tool-call budget. One call applies a list of set and remove changes in order, all or nothing. Removing a key that ends in `/` removes its subtree.
- **In the prompt**, the memory is marked as data that never overrides the instructions.
- **The Memory tab** lets you read, add, edit and delete entries once the agent has started. The form warns when the agent changed an entry you are editing; otherwise the last write of a key wins.
- **A damaged file** is copied to `.memory.json.damaged`; the readable entries are kept.

---

## The agent page

The header has a status chip, **Run now** and **New conversation** (disabled while a turn runs). The chip shows Idle, Answering, Running background or Waiting while the agent runs, otherwise Stopped, Starting, Error or Unavailable with the reason.

| Tab | Shows |
|---|---|
| Chat | The timeline: messages, replies and errors with the tools their turn called, background findings (**Show full answer** for the whole reply) and dividers. Quiet runs collapse into one line. The newest 50 rows; **Show earlier** adds 50 more |
| Activity | Run count, last run, CLI subscription usage, and the background runs still in the timeline (newest 500 entries), 25 per page, with trigger, duration, tokens, tool calls and result. Failed and cancelled runs are included, chat turns are not. The count also includes older runs and survives restarts and new conversations |
| Memory | The entries with update times and a size bar, see [Memory](#memory) |
| Settings | A summary of the settings; change them with the subject's Edit action |

- Enter sends and Shift+Enter adds a line; on touch devices Enter adds a line and the button sends.
- While a background run or an `AnthropicAgent` summary holds the agent, the status shows Waiting and your message is answered after it.
- A message to an agent that is stopped or still starting is rejected and stays in the input.

---

## Tools and permissions

Each turn offers exactly the tools in `AllowedTools`, plus `update_memory`. This is enforced where the tools are served, not by what the model is told.

| Tool | Default | What it does |
|---|---|---|
| `browse` | on | Browses the subject tree and reads property values |
| `search` | on | Finds subjects by title, path, type or property values |
| `search_markdown` | on | Finds text in Markdown pages and documents, with line and snippet |
| `get_property` | on | Reads one property value |
| `list_types` | on | Lists interfaces and concrete types, to find subjects by capability |
| `list_methods` | on | Lists a subject's methods with their kind (`query` or `operation`) and whether they require confirmation |
| `invoke_query` | on | Calls a `[Query]` method, which only reads |
| `get_property_history` | on | Reads the recorded history of a property |
| `invoke_operation` | off | Calls an `[Operation]` method, which can change devices or send something |
| `set_property` | off | Writes a property value |
| `edit_markdown` | off | Replaces one passage of a page, see [Changing pages](#changing-pages) |
| `write_markdown` | off | Replaces the whole text of a page |
| `create_markdown` | off | Creates a page in a folder |
| `edit_page_layout` | off | No tool: lets the page tools change layout blocks, see [Editing page layout](#editing-page-layout) |
| `update_memory` | always | Changes the agent's memory |
| `web_search`, `web_fetch` | off | Search the web and load pages, see [Web tools](#web-tools) |

A string result longer than 64K characters is cut and ends with a `[truncated: N characters]` line.

### Reading pages and documents

Pages and documents, such as these docs under `/Docs`, are the main context about an installation. The preamble, the tool descriptions and the external MCP server's instructions point to them.

- `ReadEvaluatedContent` of a page, through `invoke_query`, returns the text with every `{{ path }}` replaced by its value. With `includeSourcePaths` true, each value is followed by its property path, such as `21.5 (/Devices/Kitchen/Temperature)`.
- `ReadRawContent` returns the source with the expressions unevaluated. `search_markdown` searches this view.
- **Secrets stay hidden**: secret configuration values in blocks show as `***`, and a block that creates no subject, such as an error, hides all its values. Page expressions never show a secret or a page's raw `Content`, and `search_markdown` never matches one. [AI Integration](../architecture/design/ai.md#hidden-values) lists the rules.

### Changing devices

With `invoke_operation` or `set_property` allowed, the edit form marks the agent as able to change devices and send notifications. The preamble tells it to change something only when asked or when its instructions call for it, and to say what it changed. These limits always apply:

- **`set_property` never writes configuration**, for agents and external MCP clients alike. It refuses a property that is also `[Configuration]`, such as `IsEnabled` of an OPC UA server or the mode of a GPIO pin: "Property is not writable: path. It is a configuration property; set_property cannot change configuration, an operation of the subject may."
- **Agent settings** are not reachable at all, and operations of agents, such as Run now, are refused: "Agents cannot run operations of agents."
- **Operations that require confirmation** cannot be called through MCP by anyone; run them from the UI. `list_methods` marks them with `requiresConfirmation`. Examples: Wallbox reboot, firmware update, Unlock, Set ICP Max Current and charge port unlock and toggle; door lock unlock and toggle; FRITZ!Box guest WLAN on and toggle; adding and removing plugins; starting and stopping the OPC UA server and client; New conversation of an agent.

`invoke_operation` reaches further than `set_property`: an operation can change anything it touches, including configuration, such as **Set Speed** of the sample motor setting `TargetSpeed`. Grant it as carefully. It can also release a Wallbox's charge port through **Pause Charging**, see [Wallbox](../devices/Wallbox.md#operations).

### Notifications

An agent with `invoke_operation` finds `INotificationPublisher` with `list_types`, its implementations with `search`, and calls their `SendNotification` operation, see [Notifications](../development/notifications.md). Without it, the agent can only report on its page.

### Changing pages

| Tool | Operation | What it does |
|---|---|---|
| `edit_markdown` | `ReplaceText` | Replaces one exact passage that occurs once in the source |
| `write_markdown` | `ReplaceContent` | Replaces the whole source |
| `create_markdown` | `CreateMarkdownFile` of a storage or folder | Creates a file with a plain name ending in `.md` |

Each tool can be allowed on its own; the edit form marks them "Changes pages and documents". `invoke_operation` reaches the operations too, so it can change pages with the three tools off. They write like the page editor, and an open page updates as after a save. JSON and other files cannot be changed.

The operations change text only and refuse:

- **Changing blocks**: adding, changing, removing or moving any [block](pages.md#blocks), with its arguments, `json` block and everything inside, because a block creates or configures running subjects. Slot blocks such as `Footer` and style blocks such as `:: {.note}` count. Text directly after a block without a closing line belongs to it.
- **Recreating unnamed blocks**: a change that would create the subject of a block without a `#name` again, such as adding an expression or a first text before it, see [What an Edit of the Page Keeps](pages.md#what-an-edit-of-the-page-keeps). Editing existing text is fine; a `#name` given in the page editor frees the text before it.
- **Active content**: raw HTML, images and media, math and diagram blocks, element attributes such as `{onclick=...}`, abbreviation definitions such as `*[HB]: HomeBlaze`, and links other than http, https and mailto. Active content a page already has may stay or be removed.
- **Large pages**: more than 64 KB of source, counted with `***` for hidden values.

Secrets stay hidden: the operations see the `ReadRawContent` view, and a block with a hidden secret is kept by repeating it with `***`. A passage or block with a guessed value is not found or refused, so neither reveals it. One gap remains: an agent can add a link reference definition with an http or https address, which changes where a `[text][label]` link inside a block leads. [AI Integration](../architecture/design/ai.md#page-write-rules) lists every rule.

Before you allow page changes:

- The `Docs` folder is replaced on every start where a seed directory is configured, as in the container image, so agent changes there are lost. Let agents keep their pages elsewhere.
- HomeBlaze does not version pages. Keep the data folder under version control or backed up.
- Read [Security](#security).

#### Editing page layout

`edit_page_layout` ("Changes page layout" in the edit form, off by default, needs a Markdown tool) lets that agent's Markdown tools and `invoke_operation` add, change, remove and move layout blocks. The page editor, other callers and the external MCP server keep the rules above.

- **Layout blocks**: cards, tabs, flow, grid and canvas layouts, `Responsive`, expansion panels, alerts, style blocks, the value widgets `ValueTile`, `Indicator`, `Progress`, `Gauge`, `ValueList`, `SubjectTable` and `ValueMap`, `HistoryChart`, and `Widget` blocks that show an existing subject. Their types are marked `[PageLayout]`, see [Page Layout Types](../development/building-subjects.md#page-layout-types).
- **Not layout**: the controls `Switch`, `Slider`, `Select`, `Control` and `OperationButton`, and a card with an `Image`.
- **Plain values only**: a layout block holds non-secret configuration values and never a device. A card's `Href` must be relative or http, https or mailto, and a subject path in a new or changed block must lead to an existing subject.
- **Everything else stays**: every other block and its subject stay as they are, and a change that would show an error box is refused.
- **Review**: a `Widget` can show any existing subject, with its operation controls, under any title, so a page can present a device misleadingly. A refused `Widget` path tells whether a subject exists, as browsing does.

### Web tools

`web_search` and `web_fetch` let an agent search the web and load pages, such as a forecast or a device manual. Both are off by default and carry the warning "Loads untrusted web content" in the edit form. The model's backend runs them, not HomeBlaze, so HomeBlaze cannot refuse a call.

| | `ClaudeCliAgent` | `AnthropicAgent` |
|---|---|---|
| Tools | The CLI's `WebSearch` and `WebFetch`, its only built-in tools an agent can get | The API server tools `web_search_20260209` and `web_fetch_20260209`, which need a model that supports them |
| Where | `WebSearch` at Anthropic; `WebFetch` on the HomeBlaze machine, so it reaches your local network | At Anthropic, not in your local network |
| Calls per model request | No limit | At most `MaxToolCallsPerTurn` per web tool (at least one) |

- The CLI's `WebFetch` upgrades `http` to `https`, so local HTTPS services such as router or NAS admin pages, devices or HomeBlaze itself are reachable.
- Each call is recorded in the timeline with its query or URL and a result preview, and counts toward `MaxToolCallsPerTurn`; once it is used up, graph tool calls return an error.
- The preamble tells the agent that web content is data, never instructions. A page can still try to steer it, see [Security](#security).

---

## Security

- **Prompt injection**: names and values from the graph, pages and documents reach the model and can try to steer it. The prompt calls them data, but the real defense is the tool scope: allow `invoke_operation` and `set_property` only to agents whose inputs you trust.
- **Pages**: pages are the main context of people and other agents, and a `{{ path }}` expression is evaluated whenever a page is shown or read. Allow page changes, including through `invoke_operation`, only to agents whose inputs you trust. No agent can add HTML, scripts or other active content, and with `edit_page_layout` it can rearrange a page and show any subject on it, but not create, change or remove a device.
- **Web content**: with web tools, anyone who controls a page or search result the agent reads can try to steer it, a far larger group than those who can change your graph. Do not combine them with `invoke_operation`, `set_property` or the page tools unless you accept that risk; a read-only agent can at worst report something wrong. Requests also tell the site or search provider what the agent looked up, and a `ClaudeCliAgent` with `web_fetch` can be steered into your local network.
- **Secrets**: tools expose only state, which a secret cannot be, and pages and page tools never show a secret value (see [Properties Shown on Pages](../development/building-subjects.md#properties-shown-on-pages)). An `AnthropicAgent`'s API key is stored in plain text in its JSON file, like other secrets; protect the data folder.
- **Audit**: every tool call is recorded in the timeline with previews of its arguments and result.
- **CLI isolation**: see [How a turn runs the CLI](#how-a-turn-runs-the-cli).

---

## Usage and cost

Background runs usually dominate the usage. To keep them cheap:

- set a smaller `BackgroundModel` (for example `haiku` for the CLI) and a low `BackgroundEffort`,
- watch only the paths the agent needs, and raise `MinRunInterval` for values that change often,
- prefer a longer `PollInterval`, or none when change triggers are enough.

Web tools add cost: on the API, each search is billed on top of the tokens and fetched content counts as input; on the subscription they count toward the limits. A reached subscription limit pauses the agent until its reset, which can be a weekly limit of one model, such as Opus, that the five-hour and seven-day usage on the Activity tab does not show.

---

## Files

Each agent keeps its data in the `Agents` folder of the [data folder](configuration.md#data-folder):

| File | Content |
|---|---|
| `<name>.timeline.jsonl` | The timeline, newest 500 entries; tool arguments and results as previews of up to 2000 characters |
| `<name>.memory.json` | The memory entries |
| `<name>.session.json` | The chat session: the CLI session id, or the messages and summary of an `AnthropicAgent` |
| `<name>.stats.json` | The run count and last activity; without it, the count starts at 0 |
| `<name>.workdir/` | The working directory of the CLI |

- **Name**: the agent's path with slashes as underscores, so `/Agents/EnergyAdvisor` becomes `Agents_EnergyAdvisor`. A path with an underscore or a character file names cannot hold gets `__` and a hash, so `/Agents/Energy_Advisor` becomes `Agents_Energy_Advisor__` and eight hex digits.
- **Letter case**: paths that differ only in case share files on a case-insensitive file system, such as the Windows default. Give such agents names that differ in more than case.
- **Renaming or moving** the agent's file starts with new, empty files; the old ones stay until you delete them. Deleting the files resets timeline, memory, chat and run count.

---

## Troubleshooting

| Message | Cause and remedy |
|---|---|
| The Claude CLI is not logged in. Run claude once as the HomeBlaze user and log in. | Log in, see [Setting up the Claude CLI](#setting-up-the-claude-cli), then press **Run now** or save the configuration |
| The Claude CLI is not using the Claude subscription login: it takes an API key from '...' | The CLI found an API key outside its environment, such as a Console login or an `apiKeyHelper` in the managed settings, and would bill the API. Remove it, for example with `claude /logout`, and log in with the subscription |
| The Claude CLI '...' could not be started | Install Claude Code for the HomeBlaze user or set `CliPath` to its full path |
| The Claude usage limit is reached until ... | The subscription limit is used up; the message names it when the CLI does, such as "The Claude weekly Opus usage limit". Automatic runs resume after the reset. Use a smaller background model or fewer runs |
| The Claude CLI path '...' is a batch file ... | Set `CliPath` to `claude.exe` instead of the npm `claude.cmd` shim |
| The turn stopped after N model rounds, the limit set by MaxToolCallsPerTurn. | Raise `MaxToolCallsPerTurn` or narrow the instructions, see [Settings](#settings) |
| The agent MCP listener is not running. | HomeBlaze is starting or shutting down, or the listener could not bind `127.0.0.1` at startup, see the log. HomeBlaze keeps running; only CLI agents fail |
| The turn did not finish within ... | Raise `TurnTimeout`, or narrow the instructions and watch paths |
| The tool call budget of N calls for this turn is exhausted. | Shown on a tool call. Raise `MaxToolCallsPerTurn` if the agent regularly needs more |
| Set the API key of the agent. / The Anthropic API rejected the API key. | Enter a valid key in the edit form |
| Set the model of the agent. | Set a model id such as `claude-sonnet-5` |
| The Anthropic API rate limit is reached: ... | A limit of the API key; automatic runs back off, see [Failures and pauses](#failures-and-pauses) |
| The Anthropic API did not find the model | Use a model id the API knows, such as `claude-sonnet-5` |
| The model reached the output token limit of N tokens before answering. | Raise `MaxTokens` or lower the effort |
| The model paused the turn too often: ... | The API paused an `AnthropicAgent` turn with web tools more than five times. It fails as a temporary error and automatic runs back off |
| The request is too long for the model | Start a new conversation, or lower `HistoryTokenBudget` |
| The agent is not running. | The agent is stopped or still starting. Check the status and the log |
| Invalid configuration: ... | An interval is out of range, such as a poll interval above 365 days. Correct it in the edit form |
