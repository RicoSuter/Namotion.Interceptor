---
title: Security
navTitle: Security
status: In Progress
---

# Security Design

## Overview

Security in HomeBlaze covers graph-level authorization (who can read and write which properties, invoke which methods and read which history), inter-node authentication, and MCP access control. Graph authorization is implemented for the built-in AI agents, automations and the external `/mcp` endpoint; roles for people in the browser, inter-node authentication and per-client MCP identity are planned. The operator guide is [Authorization](../../administration/authorization.md).

## Graph Authorization [In Progress]

Authorization is role-based, with roles as data. The generic engine is the library `Namotion.Interceptor.Authorization`, see its [documentation](../../../../../../../../docs/authorization.md) and [design](../../../../../../../../docs/design/authorization.md): rules select members of the graph by path, type, member name and tag, roles hold allow and deny rules and include other roles, and `AuthorizationDefinition.Create` compiles the rules once and `Compile` turns the roles of one principal into an immutable policy. `HomeBlaze.Authorization` supplies what the library leaves to the application: the roles, the tags, the actions and the system rules.

| Part | Implementation |
|------|----------------|
| Actions | `read`, `write` (built into the library), `execute` and `history` (`AuthorizationActions`) |
| Tags | `HomeBlazeMemberTagProvider` tags members from the existing markers: `state`, `configuration`, `secret`, `query`, `operation`, `requires-confirmation`, `subjects`, `attribute`, `metadata`. No subject type needs an attribute of its own. The tags of a member are cached until an attribute is added to it, and only a property of type `MethodMetadata` is read |
| Built-in roles | `Reader` (read and history on every member, execute on queries), `Operator` (`Reader`, write on every member, execute on operations) and `Everything` in code (`BuiltInRoles`), always present, not editable, names reserved. They are broad: `Operator` allows every write and operation the system rules leave, so `AllowedTools` decides what an agent does; restriction is the job of custom roles |
| Custom roles | The `Roles` dictionary of the single `Authorization` subject, a `[Configuration, InlinePaths]` dictionary of `Role` subjects with `Includes`, `Allow` and `Deny`, stored in a JSON file such as `Files/Authorization.json` |
| System rules | `SystemRules`, compiled into every policy as the library's system deny rules |
| Policies | `AuthorizationService.GetPolicy(roleNames)` |
| Enforcement | `AuthorizationMcpAccessPolicy`, the `IMcpAccessPolicy` of the MCP tools, and `PropertyReadAccess` for page expressions |

### System Rules

```text
deny  [secret]                         read, write, history
deny  [configuration]                  write
deny  [requires-confirmation]          execute
deny  types: LlmAgentBase              read, write, execute, history
deny  types: Authorization, Role       read, write, execute, history
```

They apply to every non-human client, agents and `/mcp` alike, and no role lifts them, because a deny always wins. `LlmAgentBase` is named by its full type name, since `HomeBlaze.Authorization` does not reference `HomeBlaze.AI`. `McpMethodInvoker` has its own refusal of an operation that requires confirmation, checked before the policy, so the client is told the actual reason. `[ExcludeFrom]` stays separate: it decides what is offered, not who may reach it.

### Policies

`AuthorizationService` finds the `Authorization` subjects in the registry and compiles the built-in roles, the custom roles of the single `Authorization` subject, the system rules, the four actions, the tag provider and the path provider `RulePathProvider`. It builds the paths of the MCP tools (`StateAttributePathProvider`), so that a path pattern matches the paths the tools build, and names every other attribute by the name of its property, `@` and its own name, such as `Temperature@Unit` also for an attribute declared in C# as `Temperature_Unit`, and every metadata member, such as `Temperature@HomeBlaze:Metadata`, with or without a value, as the tools list metadata only while it has one and a rule must decide the first write of metadata as it decides a later one.

- **Cache**: one policy per distinct set of role names, ignoring case and order. A change of an `Authorization` or a `Role`, observed through property changes and the lifecycle, builds a new snapshot; a policy once handed out never changes. After a rebuild, `PoliciesChanged` is raised on the thread pool, on which started agents check their roles again. The service is thread-safe.
- **Concurrent edits**: when the graph or a role list changes while a snapshot is built, its enumeration throws; the snapshot is built once more, and when that fails as well, a stale snapshot of the built-in roles makes custom roles unavailable for that call, so the error never reaches an agent turn or `ApplyConfigurationAsync`.
- **Errors**: no name at all, an empty or unknown name, a role that does not compile, a custom role with a reserved or duplicate name, or a custom role while several `Authorization` subjects exist gives `AuthorizationPolicy.DenyAll` with the reason. A policy never partially applies.
- **Statuses**: each `Role` shows why it cannot be used, from compiling it on its own, a `Warning`, for a role that compiles, when a path pattern without a wildcard resolves in the graph to an entry of a folder or collection and so matches no member (the role stays usable, because the result depends on the graph at the time of the check), and how many started agents name it (`UsedByAgents`); each `Authorization` shows when it is one of several. Statuses are written in the background after a change. `GetUnavailableRoles` lists the custom roles that exist but cannot be used with the reason, which the role picker shows.
- **Single instance**: `Authorization` is `[SingleInstance]`. A second one (a copied or restored file) disables all custom roles until it is removed, while the built-in roles keep working, so two sets of roles are never merged silently.
- **No creation by clients**: an agent or `/mcp` client cannot create an `Authorization` or a `Role`, which would disable the custom roles or define roles that principals already name. The only files the tools create are Markdown pages, and the [page write rules](ai.md#page-write-rules) refuse every added block that is no layout block (`[PageLayout]`), whatever its type and however deeply it is nested; metadata files are never loaded as subjects and refuse keys that start with `$`; the create dialog of a folder lets a person pick the type; adding a plugin and starting an OPC UA client require confirmation.

### Principals

| Principal | Policy |
|-----------|--------|
| Built-in agent | Its `Roles`, default `["Operator"]`, which allows every write and operation the system rules leave, so `AllowedTools` is the gate of an agent with the default. A chat turn and a background run take `AllowedTools` once; each tool call, also one through the MCP route of a CLI agent, fetches the policy of the current `Roles` (`AuthorizationMcpAccessPolicy.BeginCall`, cached per compiled snapshot) and decides the whole call with it, so a removed role is refused at the next call while a call stays consistent. Roles that yield `DenyAll`, also an empty list, show "Invalid roles: ..." in the agent's status, and the turn runs with tools that deny everything |
| Watch paths of an agent | The agent's `Roles`, read when a background run starts, so the background message holds only what the agent's tools may read, and a watched change starts a run only when the roles allow to read the property and show its subject |
| External `/mcp` | `Everything` with the system rules, and `McpServer:ReadOnly` |
| Automation | Its `Roles`, none by default, so it calls and writes nothing until a role allows it. `RoleAutomationAuthorizer`, the default `IAutomationAuthorizer`, asks `execute` on an operation and `write` on a property. One check (`AutomationTargets`) runs when the automation applies a configuration (`ActionTargetCheck` in `AutomationCompiler`), in the editor (`AutomationValidator`, with the roles of the dialog) and before each action. A target that does not resolve yet is checked when the action runs. Roles that yield `DenyAll` refuse every call and write but do not block applying a configuration, so an automation that starts before `Authorization.json` is loaded runs once it is. While a configuration is rejected, the previous rules keep running and each action needs both the applied roles and the roles of the rejected configuration (`RejectedRolesPermissions`), so roles narrowed in a rejected file apply at once and widened ones allow nothing more. Conditions and texts read without the roles, as watch paths do |
| People in the browser | No policy yet |

### Enforcement

`HomeBlazeMcpConfigurationFactory.Create` takes the access policy and sets it on the `McpServerConfiguration`, so every tool provider, including the one of `HomeBlaze.History.Mcp`, decides with `configuration.IsAllowed`, `TryGetReadableProperty` and `TryGetReadableSubject`. The CLI agent route serves the turn's tool set, so it is covered.

| Tool | With a policy |
|------|---------------|
| `browse`, `search`, `get_property`, `set_property` | The rules of `Namotion.Interceptor.Mcp`: hidden reads as missing, writable needs `write` |
| `list_methods`, `$methods`, `$title`, `$icon` | Only methods with `read`; the title and the icon only with `read` on them when they are registered members, such as a derived title |
| `invoke_query`, `invoke_operation`, Markdown tools | Subject resolved with `TryGetReadableSubject`, else "Subject not found."; `read` on the method, else "Method not found"; `execute`, else "Operation 'Name' is not permitted. The access policy does not allow it." |
| `get_property_history` | Path resolved with `TryGetReadableProperty`, else "Path not found"; then `history`, else "History is not available: path. The access policy does not allow it."; the store is queried with the recorded path of the resolved property (`GetHistoryPath`), whichever spelling the client used |
| `search_markdown` | Pages readable at their path with `read` and `execute` on `ReadRawContent`; notes whose metadata member is readable |
| `SetMetadata` | A target that the tools list, and `write` on its metadata member; a target without metadata gets its member registered with a null value first, which is the same as no metadata |
| `SearchMetadata` | Matches whose subject the tools list and whose metadata member is readable |
| `GetOrder`, `SetOrder` | `GetOrder` names only the children that the tools list at their path, and so do the `order` in the metadata of a folder and its property `ChildOrder`, which holds the same order and which the tools do not list. A value filter of the configuration (`IMcpValueFilter`) reduces both where the built-in tools `get_property`, `browse`, `search` and `set_property` return or compare them, `McpMethodInvoker` hands the same filters to page expressions of a query through `PropertyReadAccess`, and `SearchMetadata` keeps a match only when the filtered metadata still matches, so a hidden name cannot be confirmed by searching for it. Other results of `invoke_query`, such as the text of `ReadRawContent`, are method results and are not filtered. `SetOrder` needs `write` on the metadata member of the folder, as `SetMetadata` does. An order in `SetOrder` or in the `order` of `SetMetadata` that names a child the tools do not list is refused with the storage's text for a name that is no child, which names only the children they list, so unlisted and missing cannot be told apart |

**Page expressions.** `ReadEvaluatedContent` is a query of the shared page subject, called with the MCP arguments, so the caller cannot be passed in, and agents and people read the same page concurrently. `McpMethodInvoker`, which every method and Markdown tool calls through, therefore sets an ambient read check (`PropertyReadAccess`, an `AsyncLocal`) from the configuration's access policy for the duration of each query, as `MarkdownPageLayoutPermission` does for page layout, and `MarkdownExpressionContext` resolves a property the check refuses as missing, which renders empty like a secret. Agents and `/mcp` clients get the same rule in one place, and a scope is entered for every method, without a check for an operation and without a policy, so a check of the calling flow never carries over.

- **The check** (`McpPageReadAccess`) builds the property's path from the root and requires `TryGetReadableProperty` to resolve that path back to the same property, so a property below a hidden branch renders empty although the policy allows to read it. The path names every member, the tools' segments and otherwise the member's name, so an expression may read a member the tools do not list, such as a derived title. A property without a path fails closed. The answers are cached per property for one query.
- **Values**: an expression shows the value of a readable property after the value filters of the configuration (`PropertyReadAccess.GetValue`), as `get_property` returns it, so a folder's `order` and `ChildOrder` name only the children the tools list.
- **Value maps**: the `map` pipe applies a map only when one of its members passes the check, so a map below a hidden branch, or one whose members the policy all hides, reads as a missing one.
- **Queries only**: an operation changes state, and the change handlers, renders and background work it starts would otherwise inherit the caller's check.
- Without a scope, as in the browser, nothing changes. The derived `Value` of a page expression is computed without the check, as it is shared by every caller.

**Test now of automations.** `AutomationRule.TestNow` is a query, so the read check that `McpMethodInvoker` sets for a query reaches its evaluation, which treats a property the check refuses as missing; the previews of the automation editor pass the same check, which has no scope in the browser. The running automation never uses it.

**Writing automations.** An agent or `/mcp` client can neither create nor change an automation, whatever its roles, because an automation acts with its own roles and a principal that could write one could give it more than it holds. Comparing the writer's roles with the automation's is not possible, as neither the page parser nor the automation knows who wrote a file, so every write path is closed instead: `deny [configuration] write` covers `Roles`, the rules and every other setting of an automation and of its rules; `MarkdownWriteRules` lets the Markdown tools add, change or remove only layout blocks, which hold single configuration values and no nested subjects, and `Automation` is no `[PageLayout]` type; agents have no tools that write files; and the **Create** operation of a folder runs the create dialog, which only the Blazor UI provides. `AutomationWriteAccessTests` and the automation rows of `MarkdownFileWriteTests` pin each path. A person may still place an automation in a page.

**Known limits.**

- A rule on a derived property applies to that property, even when it is computed from properties the policy does not allow to read.
- The raw source of a page (`ReadRawContent`, and `ReadEvaluatedContent` outside its expressions) includes the non-secret configuration values of its blocks, whatever the roles allow on their subjects.
- With a policy, the history of a path that does not resolve is not available.
- A subject whose only members other than attributes are secrets is empty for every policy and hidden from every agent and `/mcp` client.
- A started OPC UA server (`HomeBlaze.OpcUa`) is an anonymous read-write gateway over what it exposes and ignores roles. Starting it is an `execute` of **Start**, which requires confirmation, so agents and `/mcp` clients cannot start it.

## Inter-Node Authentication [Planned]

Planned. WebSocket connections between instances (satellite-to-central, primary-to-standby) need authentication to prevent unauthorized nodes from joining the topology.

Likely approach: mutual TLS or token-based authentication on WebSocket connections.

## MCP Access Control [Planned]

Planned. Today every `/mcp` client shares the `Everything` policy. With a client identity, the MCP session would carry roles and get its own policy from `AuthorizationService`, as an agent does.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Authorization model | Roles as data, rules selecting by path, type, member and tag, compiled per principal | Operators define roles without code; subject types need no attributes, as tags come from existing markers |
| Built-in roles | `Reader`, `Operator`, `Everything` in code | Always available, so an installation without `Authorization.json` works, and `Operator` leaves the choice of tools to `AllowedTools` |
| System rules | Fixed deny rules for every non-human client | Secrets, configuration, confirmation, agents and the roles themselves must never depend on a role being written correctly |
| Two gates for agents | `AllowedTools` decides the tools, roles what they reach | Tools stay a coarse, visible switch; roles add the fine-grained scope |
| Several `Authorization` subjects | None is used | Merging two role sets silently could grant more than either intends |
| Page expressions | Ambient `AsyncLocal` read check | The shared page subject cannot receive the caller, and a per-caller service does not reach it |
| Automations | Own roles, none by default, checked at validation and before each action | An automation acts unattended: only explicitly allowed operations run, and a deny in its role is a block that rules added later cannot bypass |
| Agents writing automations | Refused on every path, whatever the roles | The writer of a file is not known when it loads, so "only roles the writer holds" cannot be checked; agents need not author automations |
| Inter-node auth | Deferred | Focus on graph authorization first |

## Open Questions

- Roles of people in the browser, the role editor and a "preview as" tester
- Rights of the person who edits an automation, once people have roles. The previews of the automation editor refresh on timers that do not carry the ambient read check, so the read check of the person must then reach `AutomationPreview.Evaluate` and `Render` as a parameter that each component captures
- Inter-node authentication mechanism (TLS, tokens, certificates)
- MCP session identity model
- Authorization for cross-instance operation proxying
- Audit integration: logging authorization decisions (see [Audit](audit.md))
