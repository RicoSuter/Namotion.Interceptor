---
title: Security
navTitle: Security
status: In Progress
---

# Security Design

## Overview

Security in HomeBlaze covers graph-level authorization (who can read and write which properties, invoke which methods and read which history), inter-node authentication, and MCP access control. Graph authorization is implemented for the built-in AI agents and the external `/mcp` endpoint; roles for people in the browser, inter-node authentication and per-client MCP identity are planned. The operator guide is [Authorization](../../administration/authorization.md).

## Graph Authorization [In Progress]

Authorization is role-based, with roles as data. The generic engine is the library `Namotion.Interceptor.Authorization`, see its [README](../../../../../../Namotion.Interceptor.Authorization/README.md) and [design](../../../../../../../../docs/design/authorization.md): rules select members of the graph by path, type, member name and tag, roles hold allow and deny rules and include other roles, and `AuthorizationPolicy.Compile` turns the roles of one principal into an immutable policy. `HomeBlaze.Authorization` supplies what the library leaves to the application: the roles, the tags, the actions and the system rules.

| Part | Implementation |
|------|----------------|
| Actions | `read`, `write` (built into the library), `execute` and `history` (`AuthorizationActions`) |
| Tags | `HomeBlazeMemberTagProvider` tags members from the existing markers: `state`, `configuration`, `secret`, `query`, `operation`, `requires-confirmation`, `subjects`, `attribute`, `metadata`. No subject type needs an attribute of its own |
| Built-in roles | `Reader`, `Operator` and `Everything` in code (`BuiltInRoles`), always present, not editable, names reserved |
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

They apply to every non-human client, agents and `/mcp` alike, and no role lifts them, because a deny always wins. They replace the hand-written checks of the tools: "agents cannot run operations of agents", "agents cannot change the metadata of agents" and the configuration write policy. `LlmAgentBase` is named by its full type name, since `HomeBlaze.Authorization` does not reference `HomeBlaze.AI`. `McpMethodInvoker` keeps its own refusal of an operation that requires confirmation, checked before the policy, so the client is told the actual reason. `[ExcludeFrom]` stays separate: it decides what is offered, not who may reach it.

### Policies

`AuthorizationService` finds the `Authorization` subjects in the registry and compiles the built-in roles, the custom roles of the single `Authorization` subject, the system rules, the four actions, the tag provider and the path provider of the MCP tools (`StateAttributePathProvider`), so that a path pattern matches the paths the tools show.

- **Cache**: one policy per distinct set of role names, ignoring case and order. A change of an `Authorization` or a `Role`, observed through property changes and the lifecycle, builds a new snapshot; a policy once handed out never changes. The service is thread-safe.
- **Errors**: no name at all, an empty or unknown name, a role that does not compile, a custom role with a reserved or duplicate name, or a custom role while several `Authorization` subjects exist gives `AuthorizationPolicy.DenyAll` with the reason. A policy never partially applies.
- **Statuses**: each `Role` shows why it cannot be used, from compiling it on its own, and how many started agents name it (`UsedByAgents`); each `Authorization` shows when it is one of several. Statuses are written in the background after a change.
- **Single instance**: `Authorization` is `[SingleInstance]`. A second one (a copied or restored file) disables all custom roles until it is removed, while the built-in roles keep working, so two sets of roles are never merged silently.

### Principals

| Principal | Policy |
|-----------|--------|
| Built-in agent | Its `Roles`, default `["Operator"]`, which allows what agents could do before roles existed, so `AllowedTools` stays the gate on upgrade. A chat turn and a background run take the policy once, next to the `AllowedTools` snapshot. Roles that yield `DenyAll`, also an empty list, show "Invalid roles: ..." in the agent's status, and the turn runs with tools that deny everything |
| Watch paths of an agent | `Everything` with the system rules: the operator chose the paths, so the agent's roles do not filter them |
| External `/mcp` | `Everything` with the system rules, and `McpServer:ReadOnly` |
| People in the browser | No policy yet |

### Enforcement

`HomeBlazeMcpConfigurationFactory.Create` takes the access policy and sets it on the `McpServerConfiguration`, so every tool provider, including the one of `HomeBlaze.History.Mcp`, decides with `configuration.IsAllowed`, `TryGetReadableProperty` and `TryGetReadableSubject`. The CLI agent route serves the turn's tool set, so it is covered.

| Tool | With a policy |
|------|---------------|
| `browse`, `search`, `get_property`, `set_property` | The rules of `Namotion.Interceptor.Mcp`: hidden reads as missing, writable needs `write` |
| `list_methods`, `$methods` | Only methods with `read` |
| `invoke_query`, `invoke_operation`, Markdown tools | Subject resolved with `TryGetReadableSubject`, else "Subject not found."; `read` on the method, else "Method not found"; `execute`, else "Operation 'Name' is not permitted. The access policy does not allow it." |
| `get_property_history` | Path resolved with `TryGetReadableProperty`, else "Path not found"; then `history`, else "History is not available: path. The access policy does not allow it." |
| `search_markdown` | Pages shown at their path with `read` and `execute` on `ReadRawContent`; notes whose metadata member is readable |
| `SetMetadata` | A target the tools show, and `write` on its metadata member; a target without metadata gets its member registered with a null value first, which is the same as no metadata |
| `SearchMetadata` | Matches whose subject is shown and whose metadata member is readable |

Denied `execute` and `history` are logged at debug level with the agent and the member; reads and writes are asked for every listed member and are not logged.

**Page expressions.** `ReadEvaluatedContent` is a query of the shared page subject, called with the MCP arguments, so the caller cannot be passed in, and agents and people read the same page concurrently. `McpMethodInvoker`, which every method and Markdown tool calls through, therefore sets an ambient read check (`PropertyReadAccess`, an `AsyncLocal`) from the configuration's access policy for the duration of each method call, as `MarkdownPageLayoutPermission` does for page layout, and `MarkdownExpressionContext` resolves a property the check refuses as missing, which renders empty like a secret. Agents and `/mcp` clients get the same rule in one place, and the scope is set even without a policy, so a check of the calling flow never carries over. Without a scope, as in the browser, nothing changes. The derived `Value` of a page expression is computed without the check, as it is shared by every caller.

**Known limits.**

- A rule on a derived property applies to that property, even when it is computed from properties the policy does not allow to read.
- The raw source of a page (`ReadRawContent`, and `ReadEvaluatedContent` outside its expressions) includes the non-secret configuration values of its blocks, whatever the roles allow on their subjects.
- With a policy, the history of a path that does not resolve is not available.

## Inter-Node Authentication [Planned]

Planned. WebSocket connections between instances (satellite-to-central, primary-to-standby) need authentication to prevent unauthorized nodes from joining the topology.

Likely approach: mutual TLS or token-based authentication on WebSocket connections.

## MCP Access Control [Planned]

Planned. Today every `/mcp` client shares the `Everything` policy. With a client identity, the MCP session would carry roles and get its own policy from `AuthorizationService`, as an agent does.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Authorization model | Roles as data, rules selecting by path, type, member and tag, compiled per principal | Operators define roles without code; subject types need no attributes, as tags come from existing markers |
| Built-in roles | `Reader`, `Operator`, `Everything` in code | Always available, so an installation without `Authorization.json` works, and `Operator` keeps the agents' behavior on upgrade |
| System rules | Fixed deny rules for every non-human client | Secrets, configuration, confirmation, agents and the roles themselves must never depend on a role being written correctly |
| Two gates for agents | `AllowedTools` decides the tools, roles what they reach | Tools stay a coarse, visible switch; roles add the fine-grained scope |
| Several `Authorization` subjects | None is used | Merging two role sets silently could grant more than either intends |
| Page expressions | Ambient `AsyncLocal` read check | The shared page subject cannot receive the caller, and a per-caller service does not reach it |
| Inter-node auth | Deferred | Focus on graph authorization first |

## Open Questions

- Roles of people in the browser, the role editor and a "preview as" tester
- Inter-node authentication mechanism (TLS, tokens, certificates)
- MCP session identity model
- Authorization for cross-instance operation proxying
- Audit integration: logging authorization decisions (see [Audit](audit.md))
