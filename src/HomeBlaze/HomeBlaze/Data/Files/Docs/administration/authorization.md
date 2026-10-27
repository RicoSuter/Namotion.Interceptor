---
title: Authorization
navTitle: Authorization
---

# Authorization

Roles decide what the built-in [AI agents](ai-agents.md) and the clients of the external `/mcp` endpoint may read, change, run and read the history of. Each agent names its roles, and `/mcp` uses the built-in role `Everything`. Fixed system rules apply on top of every role. People in the browser have no roles yet and are not affected. The design is in [Security Design](../architecture/design/security.md#graph-authorization-in-progress).

On this page:

- [Roles and Allowed Tools](#roles-and-allowed-tools): the two gates of an agent
- [Built-in Roles](#built-in-roles): `Reader`, `Operator` and `Everything`
- [Custom Roles](#custom-roles): creating the `Authorization` subject and adding, renaming and removing roles
- [Editing a Role](#editing-a-role): the rule cards and the [preview](#previewing-a-role) of what a role allows
- [Rules and Selectors](#rules-and-selectors): what a rule matches and how rules combine
- [Actions](#actions): `read`, `write`, `execute` and `history` in the tools
- [System Rules](#system-rules): the deny rules no role can lift
- [One Authorization Subject](#one-authorization-subject): what happens with two
- [Agents](#agents): the `Roles` setting and the "Invalid roles" status
- [External MCP Clients](#external-mcp-clients), [Watch Paths](#watch-paths) and [Page Expressions](#page-expressions)
- [File Format](#file-format): the roles in `Authorization.json`
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

The built-in roles are defined in code, always available and cannot be changed. Their names are reserved, ignoring case. The edit dialog of the `Authorization` subject lists them read-only below the custom roles.

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

Custom roles live in one `Authorization` subject, and each role is a subject below it, reachable at the path of the `Authorization` subject followed by the role name, such as `/Authorization/LightOperator`. They are created and edited in the subject browser: the edit dialog of the `Authorization` subject adds, renames and removes roles, and the edit dialog of each role edits its rules. Both dialogs save into the file of the `Authorization` subject, such as `Files/Authorization.json`, which can also be written by hand, see [File Format](#file-format).

### Creating the Authorization Subject

1. Click "Create" in the subject browser, in the folder that should hold the file.
2. Click the type "Authorization" in the group "System". The dialog offers it only while no `Authorization` subject exists, see [One Authorization Subject](#one-authorization-subject).
3. Check the field "File name". The dialog suggests `Authorization`.
4. Add the first roles with "Add role", as described below, or add them later. The rules of a role are edited in its own dialog once the subject is created.
5. Click "Create".

### Adding, Renaming and Removing Roles

Select the `Authorization` subject in the subject browser and click "Edit", the pencil next to its title. The dialog lists two groups:

- **Custom roles**: each role with its name, its number of allow and deny rules, "used by N agents" and a status icon. The tooltip of the icon reads "Can be used.", the reason why the role cannot be used, or "Added; saved with this dialog." for a role that is not saved yet. A role that can be used but has a `Warning`, see [Status and Usage](#status-and-usage), shows a yellow warning icon whose tooltip reads "Can be used, but: " followed by the warning. A role added in the dialog shows "(new)" after its name, a renamed one "(was OldName)".
- **Built-in roles**: `Reader`, `Operator` and `Everything`, each with one line of what it allows. They cannot be changed, see [Built-in Roles](#built-in-roles).

To add a role:

1. Click "Add role".
2. Type the name into the field "Name".
3. Click "Add" or press Enter. The role is listed with "(new)".
4. Click "Save". The role has no rules yet, so it allows nothing; open it to add rules, see [Editing a Role](#editing-a-role).

To rename a role, click its pencil button, change the name in the field "Name", click "Rename" or press Enter, and click "Save". The role keeps its rules. To remove a role, click its trash button and click "Save". Nothing changes before "Save", and "Cancel" discards the changes of the dialog.

The field "Name" checks the name as you type, and "Add" and "Rename" stay disabled until it is valid:

- It is not empty, else "Enter a name.".
- It is not the name of a built-in role, ignoring case, else "The name 'reader' is reserved for a built-in role.".
- It is not `System`, ignoring case, else "The name 'System' is reserved for the system rules.".
- It contains no `/`, `[` or `]`, because the name is part of the role's path, else "The name 'Kitchen/Lights' cannot contain '/', '[' or ']', because it is part of the role's path.".
- No other custom role has it, ignoring case, else "A role with this name exists already.". A rename may change only the case of the name.

A name typed but not confirmed with "Add" or "Rename" is taken over by "Save", and an empty field of a role being added is ignored.

When agents or other roles still name the typed name, such as after the role of that name was removed or renamed, the field "Name" warns before you click "Add" or "Rename", for example "2 agents already name this role and will get its rules after saving." and "Kitchen (used by 1 agent) includes a role of this name and will get its rules after saving.". The warning does not block the name; it says that the role takes over where the old one was named.

### Roles That Agents Use

Agents and the custom roles that include a role name it, so a rename or removal changes what they may do. Includes are not changed for you:

- **Remove**: when agents use the role or other roles include it, the trash button asks first, such as with "2 agents use this role. After saving, they are denied everything and show "Invalid roles" until they are given another role." and "Kitchen (used by 1 agent) includes this role. After saving, it cannot be used and its agents are denied everything until its includes are changed." Click "Remove" to remove it or "Cancel" to keep it.
- **Rename**: when agents use the role or other roles include it, the field "Name" warns before you click "Rename", such as with "2 agents use this role under its old name. After saving, they are denied everything and show "Invalid roles" until they are given the new name or another role." and "Kitchen (used by 1 agent) includes this role under its old name. ...". A rename that changes only the case of the name shows no warning, as names compare ignoring case.
- **After saving**: the agents keep the old name in their `Roles`. They show the status "Invalid roles: Role 'LightOperator' is not defined." and every tool call of theirs is denied, not only the calls the role allowed, see [Invalid Roles](#invalid-roles). Their edit dialog shows the old name as a red chip with the tooltip "Unknown role". Remove it there and select the new name or another role, then save the agent.
- **Includes**: a custom role that includes the old name shows the status "Role '...' includes 'LightOperator', which is not defined." and cannot be used until its includes are changed as well, so the agents that use it are denied everything until then.

### Status and Usage

The property panel of a role shows four values:

- **`Status`**: why the role cannot be used, or empty. "The name 'Reader' is reserved for a built-in role." or "The name 'System' is reserved for the system rules." for a reserved name, where agents naming `Reader` keep the built-in role; "The name 'Kitchen/Lights' cannot contain '/', '[' or ']', because it is part of the role's path." for a name written by hand with such a character; "The name '...' is used by more than one role, ignoring case."; or why the rules do not compile, such as "Role 'LightOperator' includes 'Readers', which is not defined.", "Action 'run' of role 'LightOperator' is not a known action." or "Path pattern 'Devices' of role 'LightOperator' must start with '/'.".
- **`Warning`**: hints about rules that likely do not do what they say, or empty; the role stays usable. "Path pattern '/Devices/Kitchen' names a subject, not a member, and matches nothing; use '/Devices/Kitchen/**' for the subject and everything below it, or '/Devices/Kitchen/*' for its members." for a pattern without a wildcard that names an entry of a folder. The patterns are checked against the graph when the roles change, so a subject that appears later is found at the next change of a role. The list of roles in the `Authorization` dialog shows a warning icon for such a role, and the dialog of the role shows the warning below its name; the field "Paths" of a rule card checks a pattern against the current graph while you type, see [Rule Cards](#rule-cards).
- **`UsedByAgents`**: the number of started agents that name the role.

Role names compare ignoring case. A saved change applies to the next turn of each agent, as a whole: an agent never gets a part of the changes of one save. When a hand-written file holds two roles whose names differ only in case, the `Authorization` dialog shows "Several roles have the same name, ignoring case. Rename or remove one to save." and "Save" stays disabled until one is renamed or removed.

When the file of the `Authorization` subject is reloaded while its dialog or the dialog of one of its roles is open, such as after it was edited by hand, "Save" does not overwrite the reloaded roles. Neither does "Save" in the dialog of a role when another dialog of the same role, such as one of another user, saved it since this one was opened, so that the earlier save is not reverted. It shows "Error saving configuration: The roles changed while this dialog was open. Close it and open it again."; close the dialog, open it again and repeat the change.

---

## Editing a Role

Select the role below the `Authorization` subject in the subject browser, such as `LightOperator`, and click "Edit", the pencil next to its title. The dialog shows the name of the role, "Used by N agents", the `Status` of the role as an error when it cannot be used, its `Warning` as a warning, and the tabs "Rules" and "Preview".

This builds the role `LightOperator`, which switches and dims the lights, except in the bedroom, and otherwise only reads:

1. On the tab "Rules", open "Includes" and select `Reader`, so the agent still sees and reads everything else.
2. Under "Allow", click "Add rule". A new rule card appears.
3. In the field "Types", type `ILightbulb` and pick it from the suggestions.
4. In the field "Members", type `TurnOn, TurnOff, Toggle, SetBrightness` and press Enter. Each name becomes a chip of its own.
5. Under "Actions" of the card, check "execute".
6. Under "Deny", click "Add rule", add `/Devices/Bedroom/**` to "Paths" and `ILightbulb` to "Types", and check "execute".
7. Check the result on the tab "Preview", see [Previewing a Role](#previewing-a-role).
8. Click "Save". The role is saved into the file of the `Authorization` subject.

[File Format](#file-format) shows the same role as JSON and explains its rules.

### Rule Cards

The tab "Rules" has three parts:

- **"Includes"**: the roles whose rules this role has as well, built-in or custom. The role itself is not offered. A name that is no role, or a custom role that cannot be used, shows as a red chip with the reason as its tooltip, which removes like any other chip.
- **"Allow"**: a card per rule whose matches are allowed.
- **"Deny"**: a card per rule whose matches are denied, whichever role allowed them.

"Add rule" below each list adds an empty card, and "Remove" on a card removes that rule. A card has a field per [selector](#rules-and-selectors) and the actions:

| Field | Takes |
|-------|-------|
| "Paths" | [Path patterns](#path-patterns) of members, such as `/Devices/Sonos/*` or `/Devices/**/Volume`. The search button at the end of the field opens "Select Path" with the tree of the subjects and their members: properties, methods and attributes. A secret can be picked, so that a deny rule can name it, but its value is never shown. Pick one and click "OK" to put its pattern into the field: a subject, or a property that holds subjects, comes with `/**` appended, so that the pattern covers everything below it, and any other member comes with its own path, such as `/Devices/Sonos/Play`. Edit the pattern, such as to end in `/*` for the subject's own members only, and click "Add". A pattern is checked as you type: an invalid one shows its error under the field, such as "Path pattern 'Devices' must start with '/'.", and one that names an entry of a folder shows the warning of the role's `Warning` below the field, see [Status and Usage](#status-and-usage), and a yellow chip once added. |
| "Types" | Type names, case-sensitive. While you type, the field suggests the subject types HomeBlaze knows and the device interfaces they implement, such as `ILightbulb`, by simple name. Any other name can be typed as well, simple or full; a name that matches no known type or interface, such as `ILightBulb`, shows as a yellow chip with the tooltip "No known subject type or interface has this name; check its spelling and case." and can still be saved, for a type of a plugin that is not loaded. A name with whitespace is rejected with "A type name contains no whitespace." |
| "Members" | Member names, case-sensitive, such as `Temperature`; `*` matches any text, such as `Set*`. A name with whitespace is rejected with "A member name contains no whitespace." |
| "Tags" | [Tags](#tags), suggested while you type. Another text shows "Unknown tag; use one of state, configuration, secret, query, operation, requires-confirmation, subjects, attribute, metadata." |
| "Actions" | The checkboxes "read", "write", "execute" and "history". At least one is required, else the card shows "Select at least one action." |

Each value is a chip below its field:

- **Adding**: press Enter, click "Add" next to the field or leave the field to add the typed text. "Add" is disabled while the field is empty or holds invalid text. Separate several values with commas, such as `/Devices/Kitchen/**, /Devices/Garden/**`; each part becomes a chip, without the whitespace around it. In a field with suggestions, click a suggestion, or choose one with the arrow keys and press Enter. "Save" also adds the text still in a field, so nothing typed is lost.
- **Removing**: click the cross of the chip.
- **Invalid text**: an invalid pattern, tag or name is not added. It stays in the field with its error, and "Save" stays disabled until it is corrected or cleared. When one part of text with commas is invalid, none of its parts is added.
- **Invalid values from the file**: a value from the file that is invalid shows as a red chip with the error as its tooltip, and "Save" stays disabled until it is removed. Besides the errors above, this is a value that holds a comma, "Contains a comma, which separates values; remove it and add each part on its own.", has whitespace around it, "Has leading or trailing whitespace.", or is listed a second time in the same field, "Listed twice.". Tags count as listed twice in another case as well.
- **"any"**: a selector without values shows "any" below its field and matches every value, so a rule with only "Types" applies to every member of every subject of those types.
- **Every member**: a card whose four selectors are all empty shows "This rule applies to every member.", in red on an allow rule and as a warning on a deny rule. The rule can still be saved, but an allow rule then grants its actions on every member of every subject, as `Everything` does.
- **Deny of one member**: a deny rule with a path that has no `*` and names a property that can hold subjects, such as `/Devices/Kitchen/Lights`, shows "'/Devices/Kitchen/Lights' matches only that one member, not what is below it; add /** to deny everything below it as well.". A deny of `read` on such a property hides the subjects below it from browsing, but their members stay readable when asked for by their paths. A path that names a value, a method or an attribute shows no hint, as nothing is below it, and neither does a path with a `*` anywhere, a path that names an entry of a folder, which has the warning described under "Paths" instead, or a path that names nothing in the current graph.
- **Execute without read**: a method is listed and run only when it is readable. When the role, together with the roles it includes, allows `execute` on methods it does not allow to read, the tab "Rules" shows "Execute is allowed without read on 2 methods, such as /Devices/Sonos/Play, which therefore cannot be used. A method is listed and run only when it is readable. Allow read on them as well.", and an allow rule of the role itself with "execute" but not "read" that allows them shows a hint of its own; a rule of an included role has none. A role that allows `read` on those methods in another rule, or includes `Reader`, shows no hint.

"Save" stays disabled while a rule has no action, a value is invalid, or a field holds invalid text. An action in the file that is not one of the four shows as a red chip with the tooltip "Unknown action" and keeps "Save" disabled until it is removed.

### Previewing a Role

The tab "Preview" shows what the role allows on one subject. It uses the role as currently edited, unsaved changes included, together with the roles it includes and the [system rules](#system-rules), as an agent with only this role would get it. It changes nothing.

1. Click the tab "Preview".
2. Type the path of a subject into the field "Subject", such as `/Devices/Bedroom/CeilingLight`, or click the search button at its end, pick the subject in "Select Path" and click "OK".
3. Read the line below the field, whether the subject is shown.
4. Read the table of its members, and hover over or click a cell to see why.
5. Change a rule on the tab "Rules" and return; the preview follows the change.

The line below the field reads:

- "Shown: the subject has a readable member and a readable link from the root."
- "Hidden: no member is readable."
- "Hidden: the property Children of /Devices/Cameras is not readable.", naming a property on the way from the root that holds the subject and is not readable, such as for a role that denies `read` on `/Devices/Cameras/**`.
- "Hidden: no chain of parents leads to the root."

A path that names no subject shows "No subject has this path.", and a role that does not compile shows its error instead, such as "Role 'LightOperator' includes 'Readers', which is not defined.".

The table has a row per member: the properties, then the methods, then the attributes, named `Property@Attribute`, such as `Temperature@HomeBlaze:Metadata`. Below its name, a row shows the path a [path pattern](#path-patterns) matches the member by, such as `/Devices/Sonos/Play`; a click selects the whole path, to copy it into the field "Paths" of a rule. The columns are the [actions](#actions) "read", "write", "execute" and "history". A cell is a green tick when the action is allowed, a red cross when it is denied, and empty where the action does not apply: a method has "read" and "execute", a property or attribute "read", "write" and "history".

Hovering over a cell shows its explanation, and clicking it shows the explanation below the table, starting with the member and the action. An explanation names the role and the rule that decided:

- "Allowed by Reader: allow any member; actions read, history."
- "Denied by LightOperator: deny paths /Devices/Bedroom/**; types ILightbulb; actions execute. It overrides LightOperator: allow types ILightbulb; members TurnOn, TurnOff, Toggle, SetBrightness; actions execute."
- "Denied by a system rule: deny tags secret; actions read, write, history. It overrides Reader: allow any member; actions read, history."
- "No role allows it."

A method that can be executed but not read cannot be used, so its tick under "execute" is yellow, and its explanation ends with "It cannot be used, as read is denied: A method is listed and run only when it is readable."

For the role `LightOperator` above and a light in the bedroom, the row `TurnOn` shows a tick under "read" and a cross under "execute". Clicking the cross shows "TurnOn, execute:" followed by the second explanation: the deny rule for the bedroom removes what the allow rule for the lights granted. For a light in the kitchen the same cell is a tick with "Allowed by LightOperator: allow types ILightbulb; members TurnOn, TurnOff, Toggle, SetBrightness; actions execute.".

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
- **Edit form**: the field "Roles" offers the built-in roles and the custom roles, and each selected role shows as a chip below it. A name that is no role, such as a removed custom role or the old name of a renamed one, shows as an error chip with the tooltip "Unknown role" that can be removed, and so does a custom role that exists but cannot be used, such as one whose name another role has as well, with the reason as its tooltip. At least one role is required, as an agent without roles is denied everything.
- **Settings tab**: lists the roles, and the reason when the agent is denied everything.
- **When it applies**: each chat turn and background run takes the policy of the roles when it starts, so a change of the agent's roles, or of a role in its dialog or in `Authorization.json`, applies to the next turn.

### Invalid Roles

When the roles cannot be used, the agent shows the status Error with "Invalid roles: " and the reason, such as:

- "No roles are assigned.", for a `Roles` list left empty in the file,
- "Role 'LightOperator' is not defined.", for a name that is no built-in or custom role,
- "A role name is empty.",
- why a custom role does not compile, see [Status and Usage](#status-and-usage),
- "Role '...' is unavailable: ...", for a role with a reserved name or a name with `/`, `[` or `]`, or while [several Authorization subjects](#one-authorization-subject) exist.

Turns still run, but every tool call is denied, so the agent can tell you what it cannot do. The status is checked again when the agent starts, when its settings are saved, after each turn and when a role or the `Authorization` subject changes, so it follows a saved role or an edit of `Authorization.json` without waiting for a turn.

---

## External MCP Clients

Clients of `/mcp` use the role `Everything` with the system rules, and `McpServer:ReadOnly` still applies, see [Configuration](configuration.md#mcpserver). They therefore reach every subject but cannot read secrets, write configuration, run operations that require confirmation, reach agents, or read or change the `Authorization` subject and its roles. A role per client is not available yet.

---

## Watch Paths

The state a background run receives from the agent's `WatchPaths` is read without the agent's roles, as the operator chose the paths, but with the system rules, so the state of agents stays out. A watch path in a branch the roles hide therefore still reaches the agent in its background message. Watch only what the agent may see.

---

## Page Expressions

When an agent or an `/mcp` client reads a page through the tools, a page expression renders only a property the tools would show it at its path: the property and every property on its path readable, and every subject on the way shown. Anything else renders empty, as a secret does, and `ReadEvaluatedContent` with `includeSourcePaths` leaves out its path. So an expression below a branch the roles hide, such as `{{ /Devices/Cameras/Door/IsOpen }}` for a role that denies `read` on `/Devices/Cameras/**`, renders empty although `IsOpen` itself is readable. Unlike the tools, an expression may read a member the tools do not list, such as a derived title, when its roles allow it.

For an agent the policy comes from its roles; for `/mcp` clients it is `Everything` with the system rules, so an expression over the state of an agent renders empty for them. The check applies to queries, such as `ReadEvaluatedContent`, which evaluate pages. An operation runs without it, so the change handlers, renders and background work it starts are not restricted by the caller's roles. People see pages as before.

## File Format

The `Authorization` subject is stored as JSON, such as `Files/Authorization.json`. The dialogs write this file, and it can also be written or changed with any editor; a change of the file applies to the next turn of each agent. This file holds the role `LightOperator` built in [Editing a Role](#editing-a-role):

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

| Setting | Meaning | In the dialog |
|---------|---------|---------------|
| `includes` | The names of built-in or custom roles whose rules this role takes over, transitively | "Includes" |
| `allow` | Rules whose matches are allowed | "Allow" |
| `deny` | Rules whose matches are denied, whichever role allowed them | "Deny" |

- A rule is an object with the arrays `paths`, `types`, `members`, `tags` and `actions`, the fields of a rule card. Only `actions` is required.
- Action names, role names and tags compare ignoring case. Types, member names and path patterns compare exactly.
- An unknown property, such as `path` instead of `paths`, makes the file unreadable rather than widening a rule: it then loads as a plain JSON file without roles, and agents that name its roles get "Role '...' is not defined.".

---

## Known Limits

- **Derived properties**: a rule on a derived property applies to that property, even when it is computed from properties the roles do not allow to read. Deny the derived property as well.
- **Raw page source**: `ReadRawContent`, and so `search_markdown` and the Markdown tools, show the non-secret configuration values of the blocks of a page, even of subjects the roles hide. `ReadEvaluatedContent` evaluates only the expressions in the text and shows the blocks as written as well. Hide such pages from the agent with a `deny` of `read` on their paths.
- **History of removed properties**: `get_property_history` reads only the history of a path that resolves, so the history of a removed device is not available to agents and `/mcp` clients. The history of a resolved property is read under the path it is recorded with, whichever spelling of the path the client used.
- **Subjects with only secrets**: a subject whose only members other than attributes are secrets has no readable member, so it is hidden from every agent and `/mcp` client, as is a subject the roles deny every member of.
- **Page addresses**: `browse` and `search` give a subject with a page its address as `$url`, which is written with the shallowest path of the subject. When a member or subject on that path is hidden, `$url` is left out, so it names nothing hidden, even when the tools show the subject at another path, such as a subject that is also held below a hidden branch.
- **Roles changed while they are read**: when an edit or a reload changes the roles while they are compiled, they are read once more; when that fails as well, custom roles are unavailable for that one call, and the next call reads them again.
- **Commas in values**: a value of a rule cannot hold a comma, as typed text is split at commas. A path to a subject whose name holds a comma cannot be named in a rule; name a path above it with `**` instead.
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
