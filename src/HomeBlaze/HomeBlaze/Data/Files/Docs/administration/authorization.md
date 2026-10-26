---
title: Authorization
navTitle: Authorization
---

# Authorization

Roles decide what the built-in [AI agents](ai-agents.md) and the clients of the external `/mcp` endpoint may read, change, run and read the history of. Each agent names its roles, and `/mcp` uses the built-in role `Everything`. Fixed system rules apply on top of every role. People in the browser have no roles yet and are not affected. The design is in [Security Design](../architecture/design/security.md#graph-authorization-in-progress).

On this page:

- [Roles and Allowed Tools](#roles-and-allowed-tools): the two gates of an agent
- [Built-in Roles](#built-in-roles): `Reader`, `Operator` and `Everything`
- [Custom Roles](#custom-roles): creating `Authorization.json`
- [Rules and Selectors](#rules-and-selectors): what a rule matches and how rules combine
- [Actions](#actions): `read`, `write`, `execute` and `history` in the tools
- [System Rules](#system-rules): the deny rules no role can lift
- [One Authorization Subject](#one-authorization-subject): what happens with two
- [Agents](#agents): the `Roles` setting and the "Invalid roles" status
- [External MCP Clients](#external-mcp-clients), [Watch Paths](#watch-paths) and [Page Expressions](#page-expressions)
- [Known Limits](#known-limits)
- [Changes Against the Previous Version](#changes-against-the-previous-version)

---

## Roles and Allowed Tools

An agent passes two gates, and a call needs both:

| Gate | Decides | Set with |
|------|---------|----------|
| `AllowedTools` | Which tools a turn has, such as `invoke_operation` or `set_property` | The agent's `AllowedTools`, see [Tools and permissions](ai-agents.md#tools-and-permissions) |
| Roles | What those tools may reach: which subjects and properties they show, which properties they write, which methods they run and which history they read | The agent's `Roles` |

An agent with `invoke_operation` and only the role `Reader` cannot run an operation, and an agent with the role `Operator` but without `invoke_operation` cannot either. Roles also hide: a subject or property the roles do not allow to read is treated as if it did not exist.

---

## Built-in Roles

The built-in roles are defined in code, always available and cannot be changed. Their names are reserved, ignoring case.

| Role | Allows |
|------|--------|
| `Reader` | `read` and `history` on every member; `execute` on `query` |
| `Operator` | Everything `Reader` allows, and `write` on every member and `execute` on `operation` |
| `Everything` | Every action on every member |

The [system rules](#system-rules) apply to all three, so even `Everything` never reads a secret, writes configuration, runs an operation that requires confirmation or reaches an agent.

- **`Reader`** reads every member, also one without a marker, such as a derived title, a property without `[State]` or `[Configuration]` and non-secret configuration. The tools list only state, but such a value reaches a client through page expressions. Read on operations lets `list_methods` list them without allowing them to run.
- **`Operator`** allows exactly what an agent could do before roles existed with every tool allowed, which makes it the default of an agent: the system rules keep secrets, configuration writes, operations that require confirmation and agents out of reach, as the hand-written limits did before.
- The built-in roles are broad on purpose; restrict an agent with a custom role.
- **`Everything`** is the role of `/mcp`.

### Tags

Rules select members by tag. HomeBlaze tags each member from its markers:

| Tag | Member |
|-----|--------|
| `state` | A property marked `[State]` |
| `configuration` | A property marked `[Configuration]` |
| `secret` | A configuration property marked as secret, which also has `configuration` |
| `query` | A method marked `[Query]` |
| `operation` | A method marked `[Operation]` |
| `requires-confirmation` | An operation that requires confirmation, which also has `operation` |
| `subjects` | A property that can hold subjects, such as the children of a folder |
| `attribute` | An attribute of a property, such as its metadata |
| `metadata` | The metadata of a subject, `HomeBlaze:Metadata`, or of a property, `Temperature@HomeBlaze:Metadata`, which is also an `attribute` |

A member can have several tags, such as a property that is both `[State]` and `[Configuration]`.

---

## Custom Roles

Custom roles live in one `Authorization` subject. Create it in the subject browser (type `Authorization`, category System) or as the file `Files/Authorization.json` with any editor, then write the roles in the file. Its roles are then reachable at the path of the subject followed by the role name, such as `/Authorization/LightOperator`.

This file defines a role for an agent that switches and dims the lights, except in the bedroom, and otherwise only reads:

```json
{
  "$type": "HomeBlaze.Authorization.Authorization",
  "roles": {
    "LightOperator": {
      "includes": [ "Reader" ],
      "allow": [ { "types": [ "ILightbulb" ], "members": [ "TurnOn", "TurnOff", "Toggle", "SetBrightness" ], "actions": [ "execute" ] } ],
      "deny": [ { "paths": [ "/Devices/Bedroom/**" ], "types": [ "ILightbulb" ], "actions": [ "execute" ] } ]
    }
  }
}
```

- **`includes`** takes over the rules of `Reader`, so the agent still sees and reads everything else.
- **`allow`** runs the four operations on every subject that is an `ILightbulb`, such as a Philips Hue light. A light that does not implement `IBrightnessController` has no `SetBrightness`, so the member does not match there.
- **`deny`** removes them again for the lights at and below `/Devices/Bedroom`; `/**` makes the pattern cover the whole branch.

Give an agent this role with `"roles": ["LightOperator"]` and allow `invoke_operation`, see [Agents](#agents). The role allows no `write`, so the agent cannot write a property even with `set_property` allowed.

A role has these settings, each optional:

| Setting | Meaning |
|---------|---------|
| `includes` | The names of built-in or custom roles whose rules this role takes over, transitively |
| `allow` | Rules whose matches are allowed |
| `deny` | Rules whose matches are denied, whichever role allowed them |

The property panel of a role shows three values:

- **`Status`**: why the role cannot be used, or empty. "The name 'Reader' is reserved for a built-in role." or "The name 'System' is reserved for the system rules." for a reserved name, where agents naming `Reader` keep the built-in role; "The name '...' is used by more than one role, ignoring case."; or why the rules do not compile, such as "Role 'LightOperator' includes 'Readers', which is not defined.", "Action 'run' of role 'LightOperator' is not a known action." or "Path pattern 'Devices' of role 'LightOperator' must start with '/'.".
- **`Warning`**: hints about rules that likely do not do what they say, or empty; the role stays usable. "Path pattern '/Devices/Kitchen' names a subject, not a member, and matches nothing; use '/Devices/Kitchen/**' for the subject and everything below it, or '/Devices/Kitchen/*' for its members." for a pattern without a wildcard that names an entry of a folder. The patterns are checked against the graph when the roles change, so a subject that appears later is found at the next change of a role.
- **`UsedByAgents`**: the number of started agents that name the role.

Role names compare ignoring case. A change of the file applies to the next turn of each agent.

---

## Rules and Selectors

A rule has four selectors and a list of actions. It applies to an action on a member when the action is listed and every selector that is not empty matches. Within one selector, any value may match.

| Selector | Matches when |
|----------|--------------|
| `paths` | A pattern matches the path of the member, see below |
| `types` | The subject is of a type or implements an interface with that name, as a full name (`HomeBlaze.Abstractions.Devices.Light.ILightbulb`) or a simple one (`ILightbulb`) |
| `members` | The member's name matches; `*` matches any text. A method is named as `list_methods` shows it (`TurnOn`), an attribute `Property@Attribute`, such as `Temperature@HomeBlaze:Metadata` |
| `tags` | The member has every listed [tag](#tags). For "any of these tags", write one rule per tag |

Rules combine like this:

1. Nothing is allowed to begin with.
2. Everything any allow rule of the roles matches is added. Naming several roles is the same as one role that includes them all.
3. Everything any deny rule of the roles, or any [system rule](#system-rules), matches is removed. A role cannot undo a deny of a role it includes, so reusable roles should mostly allow.

A subject is shown when at least one of its members is readable and the properties that hold it, on the way from the root, are readable. A `deny` of `read` on a branch therefore hides its subjects, for example for an agent that may operate everything but the cameras:

```json
"Household": {
  "includes": [ "Operator" ],
  "deny": [ { "paths": [ "/Devices/Cameras/**" ], "actions": [ "read", "write", "execute", "history" ] } ]
}
```

A role that does not include `Reader` must also allow `read` on the properties that hold subjects along the way, for example with a rule on the tag `subjects`, or nothing below the root is shown.

### Path Patterns

A path pattern is written as the tools write paths, with `/` and `[InlinePaths]` flattened, and matches the paths of members exactly, like a file glob. A member is a property, a method or an attribute, so `/Devices/Sonos/Volume` is one property and `/Devices/Sonos/Play` one method. Only `*` and `**` are wildcards:

| Pattern | Matches |
|---------|---------|
| `/Devices/Sonos/Play` | That one member |
| `/Devices/Sonos/*` | Every member of that subject; `*` alone is one segment |
| `/Devices/Sonos/**` | The subject's members and everything below them; `**` is zero or more segments |
| `/Devices/Kitchen/Lights` | The property that holds the kitchen's lights, not the lights themselves |
| `/Devices/**/Volume` | Every `Volume` at any depth under `Devices` |
| `/Devices/Light*/IsOn` | `IsOn` of `/Devices/LightA`, `/Devices/Lights` and so on |
| `/Line/Machines[*]/**` | Every item of a collection or dictionary and everything below it; `Machines[CNC*]` works too |
| `/**` | Every member with a path |

To cover a subject, end the pattern with `/**`, or with `/*` for its own members only. A pattern that names a subject, such as `/Devices/Kitchen`, matches at most the property that holds it, and nothing when the subject is an entry of a folder, such as a device in `/Devices`; the role's `Warning` points this out. A `deny` of `read` on a property that holds subjects, such as `/Devices/Kitchen/Lights`, hides those subjects and everything below them, since a subject is shown only when the properties that hold it are readable. A pattern that can match no member, such as `/` or one that ends with an index like `/Line/Machines[CNC1]`, makes the role fail to compile with a hint to add `/**` or `/*`.

Attributes are matched by the path the tools give them. The [library documentation](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/src/HomeBlaze/Namotion.Interceptor.Authorization/README.md) has the complete pattern rules, including subjects with several paths. A deny rule that has only `paths` is checked against every member it matches, so keep its patterns narrow or give it another selector when you can.

### Writing Rules in JSON

- A rule is an object with the arrays `paths`, `types`, `members`, `tags` and `actions`. Only `actions` is required.
- Action names, role names and tags compare ignoring case. Types, member names and path patterns compare exactly.
- An unknown property, such as `path` instead of `paths`, makes the file unreadable rather than widening a rule: it then loads as a plain JSON file without roles, and agents that name its roles get "Role '...' is not defined.".

---

## Actions

| Action | In the tools |
|--------|--------------|
| `read` | `browse`, `search` and `get_property` show the property or subject. `list_methods` and `$methods` list a method only with `read` on it, and `invoke_query` and `invoke_operation` report a method without it as "Method not found: ...". Page expressions, `search_markdown` notes, `SearchMetadata` matches and the children `GetOrder` names need it as well |
| `write` | `set_property` writes the property, else "Property is not writable: /path. The access policy does not allow it.". `SetMetadata` needs `write` on the metadata member of its target, else "Metadata of 'path' is not writable. The access policy does not allow it.", and `SetOrder` on the metadata member of its folder, else "Metadata of '/path' is not writable. The access policy does not allow it." |
| `execute` | `invoke_query` and `invoke_operation` run the method, else "Query 'Name' is not permitted. The access policy does not allow it." or "Operation 'Name' is not permitted. ...". The Markdown tools run `ReplaceText`, `ReplaceContent` and `CreateMarkdownFile`, and `search_markdown` searches a page only with `execute` on its `ReadRawContent` |
| `history` | `get_property_history` reads the history of the property, else "History is not available: /path. The access policy does not allow it." A path the roles do not allow to read gives "Path not found: /path" |

A subject the roles hide gives "Subject not found." in the method and Markdown tools, "Path not found: ..." in `get_property`, and "No subject at '...' in this storage." in `SetMetadata`. `GetOrder` of a folder leaves it out, and an order in `SetOrder` or in the `order` of `SetMetadata` that names it is refused as a name that is no child, with only the shown children listed. `list_types`, `update_memory` and the web tools do not touch the graph and are not affected. A denied `execute` or `history` of an agent is logged at debug level with the agent and the member; denied reads and writes are not logged, as the tools ask them for every member they list.

---

## System Rules

These deny rules apply to every agent and to `/mcp`, whatever the roles allow:

| Deny | Actions | Keeps clients from |
|------|---------|--------------------|
| Members tagged `secret` | `read`, `write`, `history` | Reading or changing passwords and API keys |
| Members tagged `configuration` | `write` | Changing the settings of devices, services and agents through `set_property` |
| Members tagged `requires-confirmation` | `execute` | Running an operation that a person must confirm, such as unlocking a door |
| Agents (`LlmAgentBase` and every type derived from it) | All four | Reading, starting, resetting or changing agents, including themselves |
| `Authorization` and `Role` | All four | Reading or granting themselves roles |

They cannot be changed, because each closes a way for a client to gain more than its roles grant: a secret would leak, a configuration write could change an agent's `AllowedTools` or a device's address, a confirmation would be bypassed, and an agent that reaches agents or the roles could raise its own permissions. A deny always wins, so no role, `Everything` included, lifts them.

- **Confirmation first**: an operation that requires confirmation is refused with "'Name' requires confirmation by a person and cannot be called through MCP; run it from the HomeBlaze UI.", which names the actual reason. `list_methods` marks such methods with `requiresConfirmation`.
- **Effects of operations**: the rules decide about members, not about what an operation does. An operation may still change configuration as part of its work, such as **Set Speed** of the sample motor setting `TargetSpeed`, so grant `execute` on operations as carefully as `invoke_operation`.

---

## One Authorization Subject

Only one `Authorization` subject may exist. The create dialog offers the type only while there is none. A second one can still appear, for example from a copied or restored file. Then:

- neither is used, and each, with each of its roles, shows the status "Only one Authorization subject is allowed; remove the others. Custom roles are unavailable until then.",
- the built-in roles keep working,
- an agent that names a custom role is denied everything with "Invalid roles: Role 'LightOperator' is unavailable: Only one Authorization subject is allowed; ...", until one of them is removed.

---

## Agents

Each agent has the setting `Roles`, a list of role names:

```json
{
  "$type": "HomeBlaze.AI.Agents.ClaudeCli.ClaudeCliAgent",
  "name": "Light Assistant",
  "roles": ["LightOperator"],
  "allowedTools": ["browse", "search", "get_property", "list_methods", "invoke_query", "invoke_operation"]
}
```

- **Default**: `["Operator"]`, for a new agent and for a file without the setting, so an agent created before roles existed keeps what it could do and `AllowedTools` stays its gate. Restrict an agent with `Reader` or a custom role.
- **Edit form**: the Roles section offers the built-in roles and the custom roles. A name that is no role, such as a removed custom role, shows as an error chip that can be removed, and so does a custom role that exists but cannot be used, such as one whose name another role has as well, with the reason as its tooltip. At least one role is required, as an agent without roles is denied everything.
- **Settings tab**: lists the roles, and the reason when the agent is denied everything.
- **When it applies**: each chat turn and background run takes the policy of the roles when it starts, so a change of the roles or of `Authorization.json` applies to the next turn.

### Invalid Roles

When the roles cannot be used, the agent shows the status Error with "Invalid roles: " and the reason, such as:

- "No roles are assigned.", for a `Roles` list left empty in the file,
- "Role 'LightOperator' is not defined.", for a name that is no built-in or custom role,
- "A role name is empty.",
- why a custom role does not compile, see [Custom Roles](#custom-roles),
- "Role '...' is unavailable: ...", for a role with a reserved name or while [several Authorization subjects](#one-authorization-subject) exist.

Turns still run, but every tool call is denied, so the agent can tell you what it cannot do. The status is checked again when the agent starts, when its settings are saved, after each turn and when a role or the `Authorization` subject changes, so it follows an edit of `Authorization.json` without waiting for a turn.

---

## External MCP Clients

Clients of `/mcp` use the role `Everything` with the system rules, and `McpServer:ReadOnly` still applies, see [Configuration](configuration.md#mcpserver). They therefore reach every subject but cannot read secrets, write configuration, run operations that require confirmation, reach agents, or read or change the `Authorization` subject and its roles. A role per client is not available yet.

---

## Watch Paths

The state a background run receives from the agent's `WatchPaths` is read without the agent's roles, as the operator chose the paths, but with the system rules, so the state of agents stays out. A watch path in a branch the roles hide therefore still reaches the agent in its background message. Watch only what the agent may see.

---

## Page Expressions

When an agent or an `/mcp` client reads a page through the tools, a page expression renders only a property the tools would show it at its path: the property and every property on its path readable, and every subject on the way shown. Anything else renders empty, as a secret does, and `ReadEvaluatedContent` with `includeSourcePaths` leaves out its path. So an expression below a branch the roles hide, such as `{{ /Devices/Cameras/Door/IsOpen }}` for a role that denies `read` on `Cameras`, renders empty although `IsOpen` itself is readable. Unlike the tools, an expression may read a member the tools do not list, such as a derived title, when its roles allow it.

For an agent the policy comes from its roles; for `/mcp` clients it is `Everything` with the system rules, so an expression over the state of an agent renders empty for them. The check applies to queries, such as `ReadEvaluatedContent`, which evaluate pages. An operation runs without it, so the change handlers, renders and background work it starts are not restricted by the caller's roles. People see pages as before.

---

## Known Limits

- **Derived properties**: a rule on a derived property applies to that property, even when it is computed from properties the roles do not allow to read. Deny the derived property as well.
- **Raw page source**: `ReadRawContent`, and so `search_markdown` and the Markdown tools, show the non-secret configuration values of the blocks of a page, even of subjects the roles hide. `ReadEvaluatedContent` evaluates only the expressions in the text and shows the blocks as written as well. Hide such pages from the agent with a `deny` of `read` on their paths.
- **History of removed properties**: `get_property_history` reads only the history of a path that resolves, so the history of a removed device is not available to agents and `/mcp` clients. The history of a resolved property is read under the path it is recorded with, whichever spelling of the path the client used.
- **Subjects with only secrets**: a subject whose only members other than attributes are secrets has no readable member, so it is hidden from every agent and `/mcp` client, as is a subject the roles deny every member of.
- **Page addresses**: `browse` and `search` give a subject with a page its address as `$url`, which is written with the shallowest path of the subject. When a member or subject on that path is hidden, `$url` is left out, so it names nothing hidden, even when the tools show the subject at another path, such as a subject that is also held below a hidden branch.
- **Roles changed while they are read**: when an edit or a reload changes the roles while they are compiled, they are read once more; when that fails as well, custom roles are unavailable for that one call, and the next call reads them again.
- **Watch paths**: see [Watch Paths](#watch-paths).

---

## Changes Against the Previous Version

- **Agents are hidden** from agents and from `/mcp` clients: `browse` and `search` leave them out, `get_property` reports "Path not found", and the method tools report "Subject not found." instead of "Agents cannot run operations of agents.". Before, both could read the state of agents, and `/mcp` clients could run their operations, such as Run now.
- **Metadata and notes of agents**: `SetMetadata` for an agent or a subject inside one reports "No subject at '...' in this storage." instead of "Agents cannot change the metadata of agents.", and `SearchMetadata` and `search_markdown` leave them out for `/mcp` clients as well.
- **The `Authorization` subject and its roles** are not reachable through MCP.
- **History**: agents and `/mcp` clients no longer get the history of a path that does not resolve.
- **Page expressions** of an agent or an `/mcp` client over a property its policy does not allow to read, or below a branch it hides, render empty, such as the state of an agent for `/mcp` clients.
- **Order of folders**: `GetOrder` leaves out children the policy hides, such as agents, and `SetOrder` needs `write` on the metadata of the folder.
- **Watch paths** no longer include the state of agents.
- **Unchanged**: a refused configuration write still reads "Property is not writable: /path. The access policy does not allow it.", and an agent with the default role `Operator` can do what it could before.

---

## Related Documentation

- [AI Agents](ai-agents.md) - The agents and their `AllowedTools`
- [Configuration](configuration.md#mcpserver) - The external MCP server
- [Security Design](../architecture/design/security.md) - The design of graph authorization
