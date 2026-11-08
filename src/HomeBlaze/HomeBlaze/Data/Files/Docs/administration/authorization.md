---
title: Authorization
navTitle: Authorization
---

# Authorization

Roles decide what the built-in [AI agents](ai-agents.md) and the clients of the external `/mcp` endpoint may read, change, run and read the history of. Each agent names its roles, and `/mcp` uses the built-in role `Everything`. Fixed system rules apply on top of every role. People in the browser have no roles yet and are not affected. The design is in [Security Design](../architecture/design/security.md#graph-authorization-in-progress).

On this page:

- [Roles and Allowed Tools](#roles-and-allowed-tools): the two gates of an agent
- [Built-in Roles](#built-in-roles): `Reader`, `Operator` and `Everything`
- [Custom Roles](#custom-roles): creating the `Authorization` subject and adding, renaming and deleting roles
- [Editing a Role](#editing-a-role): the rule cards and the [preview](#previewing-a-role) of what a role allows
- [Rules and Selectors](#rules-and-selectors): what a rule matches and how rules combine
- [Actions](#actions): `read`, `write`, `execute` and `history` in the tools
- [System Rules](#system-rules): the deny rules no role can lift
- [One Authorization Subject](#one-authorization-subject): what happens with two
- [Agents](#agents): the `Roles` setting and the "Invalid roles" status
- [External MCP Clients](#external-mcp-clients), [Watch Paths](#watch-paths) and [Page Expressions](#page-expressions)
- [File Format](#file-format): the roles in `Authorization.json`
- [Known Limits](#known-limits)

---

## Roles and Allowed Tools

An agent passes two gates, and a call needs both:

| Gate | Decides | Set with |
|------|---------|----------|
| `AllowedTools` | Which tools a turn has, such as `invoke_operation` or `set_property` | The agent's `AllowedTools`, see [Tools and permissions](ai-agents.md#tools-and-permissions) |
| Roles | What those tools may reach: which subjects and properties are readable for them, which properties they write, which methods they run and which history they read | The agent's `Roles` |

An agent with `invoke_operation` and only the role `Reader` cannot run an operation, and an agent with the role `Operator` but without `invoke_operation` cannot either. Roles also hide: a subject or property the roles do not allow to read is treated as if it did not exist.

---

## Built-in Roles

The built-in roles are defined in code, always available and cannot be changed. Their names are reserved, ignoring case. The tab "Roles" of the edit dialog of a role describes them below "Includes".

| Role | Allows |
|------|--------|
| `Reader` | `read` and `history` on every member; `execute` on `query` |
| `Operator` | Everything `Reader` allows, and `write` on every member and `execute` on `operation` |
| `Everything` | Every action on every member |

The [system rules](#system-rules) apply to all three, so even `Everything` never reads a secret, writes configuration, runs an operation that requires confirmation or reaches an agent.

- **`Reader`** reads every member, also one without a marker, such as a derived title, a property without `[State]` or `[Configuration]` and non-secret configuration. The tools list only state, but such a value reaches a client through page expressions. Read on operations lets `list_methods` list them without allowing them to run.
- **`Operator`** allows every write and every operation that the system rules leave, which makes it the default of an agent: `AllowedTools` decides which tools the agent has, and the system rules keep secrets, configuration writes, operations that require confirmation and agents out of reach.
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

Custom roles live in one `Authorization` subject, and each role is a subject below it, reachable at the path of the `Authorization` subject followed by the role name, such as `/Authorization/LightOperator`. They are created and edited in the subject browser: the operation "Add Role" of the `Authorization` subject adds a role, the operation "Rename" of a role renames it, the Delete button of its pane deletes it, and the edit dialog of a role edits its rules. Each of them saves into the file of the `Authorization` subject, such as `Files/Authorization.json`, which can also be written by hand, see [File Format](#file-format).

### Creating the Authorization Subject

1. Click "Create" in the subject browser, in the folder that should hold the file.
2. Click the type "Authorization" in the group "System". The dialog offers it only while no `Authorization` subject exists, see [One Authorization Subject](#one-authorization-subject).
3. Check the field "File name". The dialog suggests `Authorization`.
4. Click "Create". The subject has no roles yet; add them as described below.

### Adding, Renaming and Deleting Roles

Select the `Authorization` subject in the subject browser. Its pane lists the custom roles under "Roles" and has the operation "Add Role"; the subject has no edit dialog. The pane of a role, titled with its name, has the operation "Rename", the "Edit" pencil and the Delete button, and shows the `Status`, `Warning` and `UsedByAgents` of the role, see [Status and Usage](#status-and-usage). The built-in roles are not listed there; the tab "Roles" of the edit dialog of a role describes them.

To add a role:

1. Click "Add Role" in the pane of the `Authorization` subject. The dialog says what adding a role does to the names in use: "Adds a custom role without rules under the given name. Its rules are edited in its own dialog. Agents and roles that already name it get its rules.", see [Roles That Agents Use](#roles-that-agents-use).
2. Type the name into the field "Name".
3. Click "Execute". The role is saved at once. It has no rules yet, so it allows nothing; open it to add rules, see [Editing a Role](#editing-a-role).

To rename a role:

1. Click "Rename" in its pane. The dialog first says what a rename does to the names in use: "Gives the role another name and keeps its rules. Agents and roles that name it by its old name report it as unknown until they are given the new name. Agents and roles that already name the new name get its rules.", see [Roles That Agents Use](#roles-that-agents-use).
2. Type the new name into the field "Name".
3. Click "Execute" to rename the role, or "Cancel" to keep it. The role keeps its rules and is saved at once.

To delete a role, click the Delete button in its pane. The confirmation asks "Delete 'LightOperator'?" with the name of the role and warns "Deletes the role. Agents and roles that name it report it as unknown afterwards.", see [Roles That Agents Use](#roles-that-agents-use); click "Delete" to delete the role, or "Cancel" to keep it. The file is saved at once and the pane of the role closes.

The name is checked when "Execute" is clicked. An invalid name changes nothing and shows "Add Role failed with an error." or "Rename failed with an error." with the reason:

- It is not empty, else "Enter a name.".
- It is not the name of a built-in role, ignoring case, else "The name 'reader' is reserved for a built-in role.".
- It is not `System`, ignoring case, else "The name 'System' is reserved for the system rules.".
- It contains no `/`, `[` or `]`, because the name is part of the role's path, else "The name 'Kitchen/Lights' cannot contain '/', '[' or ']', because it is part of the role's path.".
- It contains no invisible character, such as a zero-width space pasted with the name, or a control character, else "The name '...' cannot contain invisible characters, such as a zero-width space or a control character.". This includes the zero-width non-joiner and joiner, as the authorization library refuses them in a role name. Spaces before and after the name are removed.
- No other custom role has it, ignoring case, else "A role with this name exists already.". A rename may change only the case of the name, and a rename to the same name changes nothing.

When the file holds two roles whose names differ only in case, such as after it was written by hand, "Add Role" fails with "Several roles are named 'KITCHEN', ignoring case. Rename or delete one of them first."; "Rename" and the Delete button of one of them work.

A change that cannot be saved stays applied until the next restart, and the operation, or "Delete Failed" for a deletion, shows an error that says so. When the file cannot be written, it reads, for example, "The role 'LightOperator' was added, but the configuration could not be saved (...) and will be lost on restart.", with the error of the storage in the parentheses. When no file holds the `Authorization` subject, it reads, for example, "The role 'LightOperator' was added, but the configuration was not saved and will be lost on restart.".

"Rename" and the Delete button of a role that was deleted, or whose file was reloaded, after its pane was opened change nothing and fail with "The role is not part of an Authorization subject. It may have been deleted, or its file reloaded.". "Add Role" of an `Authorization` subject that was deleted after its pane was opened fails with "The Authorization subject is not part of the graph. It may have been deleted.". Open the subject again in the subject browser.

### Roles That Agents Use

Agents and the custom roles that include a role name it, so a rename or deletion changes what they may do. Neither a rename nor a deletion changes those names for you. The dialog of "Rename" and the confirmation of the Delete button say so, see [Adding, Renaming and Deleting Roles](#adding-renaming-and-deleting-roles). The number of agents that use a role is its `UsedByAgents`, and its edit dialog shows "Used by N agents".

- **Agents**: the agents keep the old name in their `Roles`. They show the status "Invalid roles: Role 'LightOperator' is not defined." and every tool call of theirs is denied, not only the calls the role allowed, see [Invalid Roles](#invalid-roles). Their edit dialog shows the old name as a red chip with the tooltip "Unknown role". Remove it there and select the new name or another role, then save the agent. A rename that changes only the case of the name changes nothing for them, as names compare ignoring case.
- **Includes**: a custom role that includes the old name shows the status "Role '...' includes 'LightOperator', which is not defined." and cannot be used until its includes are changed as well, so the agents that use it are denied everything until then.
- **Names still in use**: a role added or renamed under a name that agents or other roles still name, such as the name of a deleted role, takes over where that name is used, and those agents and roles get its rules. A rename can therefore grant access, which its dialog says before "Execute" is clicked.

### Status and Usage

The property panel of a role shows these values:

- **`Status`**: why the role cannot be used, or empty. "The name 'Reader' is reserved for a built-in role." or "The name 'System' is reserved for the system rules." for a reserved name, where agents naming `Reader` keep the built-in role; "The name 'Kitchen/Lights' cannot contain '/', '[' or ']', because it is part of the role's path." or "The name '...' cannot start or end with whitespace." or "The name '...' cannot contain invisible characters, such as a zero-width space or a control character." for a name written by hand with such a character; "The name '...' is used by more than one role, ignoring case."; or why the rules do not compile, such as "Role 'LightOperator' includes 'Readers', which is not defined.", "Action 'run' of role 'LightOperator' is not a known action." or "Path pattern 'Devices' of role 'LightOperator' must start with '/'.".
- **`Warning`**: hints about rules that likely do not do what they say, or empty; the role stays usable. "Path pattern '/Devices/Kitchen' names a subject, not a member, and matches nothing; use '/Devices/Kitchen/**' for the subject and everything below it, or '/Devices/Kitchen/*' for its members." for a pattern without a wildcard that names an entry of a folder. The patterns are checked against the graph when the roles change, so a subject that appears later is found at the next change of a role. The edit dialog of the role shows above its tabs the warning the role would have with its unsaved changes; it and the field "Paths" of a rule card check a pattern against the current graph while you edit, see [Rule Cards](#rule-cards).
- **`UsedByAgents`**: the number of started agents that name the role.

Role names compare ignoring case. A change applies to the next tool call of each agent, also within a running turn, as a whole: an agent never gets a part of one change.

When the file of the `Authorization` subject is reloaded while the edit dialog of one of its roles is open, such as after it was edited by hand, or the role is deleted meanwhile, "Save" neither overwrites the reloaded roles nor brings the role back. Neither does "Save" when another dialog of the same role, such as one of another user, saved it since this one was opened, so that the earlier save is not reverted. It shows "Error saving configuration: The roles changed while this dialog was open. Close it and open it again."; close the dialog, open it again and repeat the change. A role renamed while its dialog is open is saved under its new name.

A save sets the includes, the allow rules and the deny rules one after the other, and a rename changes the roles in two steps. When a change of another user saves the file between two of these steps, and the server stops or the save of this change fails before it is written, the file holds only a part of the change. Open the role after a restart, or after such a failed save, and check its rules.

---

## Editing a Role

Select the role below the `Authorization` subject in the subject browser, such as `LightOperator`, and click "Edit", the pencil next to its title. The dialog is titled after the role, such as "Edit LightOperator". Above its tabs it shows the `Status` the role would have once saved as an error when it could not be used, its `Warning` as a warning, both with its unsaved changes, and "Used by N agents". These follow the changes in the dialog, the roles saved meanwhile, such as an included role saved by another user, and the agents that start or stop using the role. The tabs are "Roles", "Allow (n)", "Deny (n)" and "Preview". The numbers count the allow and deny rules, and a red mark after a number says that one of those rules is invalid; a red mark after "Roles" says that "Includes" names a role that cannot be used. The dialog keeps its height, and only the content of the active tab scrolls.

This builds the role `LightOperator`, which switches and dims the lights, except in the bedroom, and otherwise only reads:

1. On the tab "Roles", open "Includes" and select `Reader`, so the agent still sees and reads everything else.
2. On the tab "Allow", click "Add rule". A new rule card appears.
3. In the field "Types", type `ILightbulb` and pick it from the suggestions.
4. In the field "Members", type `TurnOn, TurnOff, Toggle, SetBrightness` and press Enter. Each name becomes a chip of its own.
5. Under "Actions" of the card, check "execute".
6. On the tab "Deny", click "Add rule", add `/Devices/Bedroom/**` to "Paths" and `ILightbulb` to "Types", and check "execute".
7. Check the result on the tab "Preview", see [Previewing a Role](#previewing-a-role).
8. Click "Save". The role is saved into the file of the `Authorization` subject.

[File Format](#file-format) shows the same role as JSON and explains its rules.

### Rule Cards

The rules are edited on three tabs:

- **"Roles"**: the field "Includes", the roles whose rules this role has as well, built-in or custom. The role itself is not offered. A name that is no role, or a custom role that cannot be used, shows as a red chip with the reason as its tooltip, which removes like any other chip. Below it, under "Built-in roles", `Reader`, `Operator` and `Everything` are described in one line each, with the note that the system rules apply to every role.
- **"Allow"**: a card per rule whose matches are allowed.
- **"Deny"**: a card per rule whose matches are denied, whichever role allowed them.

"Add rule" below each list adds an empty card, and "Remove" on a card removes that rule. While a list is empty, "No allow rules yet" or "No deny rules yet" shows next to "Add rule". A card has a field per [selector](#rules-and-selectors) and the actions:

| Field | Takes |
|-------|-------|
| "Paths" | [Path patterns](#path-patterns) of members, such as `/Devices/Sonos/*` or `/Devices/**/Volume`. The search button next to the field, named "Browse Paths" for screen readers, opens "Select Path" with the tree of the subjects and their members: properties, methods and attributes. A secret can be picked, so that a deny rule can name it, but its value is not shown, nor is the value of a record or object that has a secret property by its declared property types; a secret held in a property typed as `object`, an interface or a base type, in a field or in a tuple is not detected and is shown. Pick one and click "OK" to put its pattern into the field: a subject, or a property that holds subjects, comes with `/**` appended, so that the pattern covers everything below it, and any other member comes with its own path, such as `/Devices/Sonos/Play`. Edit the pattern, such as to end in `/*` for the subject's own members only, and click "Add". A pattern is checked as you type: an invalid one shows its error under the field, such as "Path pattern 'Devices' must start with '/'.", and one that names an entry of a folder shows the warning of the role's `Warning` below the field, see [Status and Usage](#status-and-usage), and a yellow chip once added. |
| "Types" | Type names, case-sensitive. While you type, the field suggests the subject types HomeBlaze knows and the device interfaces they implement, such as `ILightbulb`, by simple name. Any other name can be typed as well, simple or full; a name that matches no known type or interface, such as `ILightBulb`, shows as a yellow chip with the tooltip "No known subject type or interface has this name; check its spelling and case." and can still be saved, for a type of a plugin that is not loaded. A name with whitespace is rejected with "A type name contains no whitespace." |
| "Members" | Member names, case-sensitive, such as `Temperature`; `*` matches any text, such as `Set*`. A name with whitespace is rejected with "A member name contains no whitespace." |
| "Tags" | [Tags](#tags), suggested while you type. Another text shows "Unknown tag; use one of state, configuration, secret, query, operation, requires-confirmation, subjects, attribute, metadata." |
| "Actions" | The checkboxes "read", "write", "execute" and "history". At least one is required, else the card shows "Select at least one action." |

At their top, the tabs "Allow" and "Deny" say "In each field, press Enter, click Add or leave the field to add the typed text. Types, Members and Tags take several values separated by commas.". Each value is a chip below its field:

- **Adding**: press Enter, click "Add" next to the field or leave the field to add the typed text. "Add" is disabled while the field is empty or holds invalid text. In "Types", "Members" and "Tags", separate several values with commas, such as `TurnOn, TurnOff`; each part becomes a chip, without the whitespace around it. "Paths" takes one path at a time and keeps its commas, as a name in a path can hold one, such as `/Devices/Living room, left/**`. A path with a comma followed by `/` or by a space and `/`, such as pasted `/Devices/A/**,/Devices/B/**`, shows "Looks like several paths separated by a comma, which is part of a path here; add each path on its own." as a warning or, when the path is invalid, after its error. In a field with suggestions, click a suggestion, or choose one with the arrow keys and press Enter. "Save" also adds the text still in a field, so nothing typed is lost.
- **Removing**: click the cross of the chip, or reach it with Tab and press Enter; screen readers name it "Remove" followed by the value. Backspace in an empty field removes its last chip; holding it down removes only that one. Removing the last chip with its cross moves the focus to the field.
- **Invalid text**: an invalid pattern, tag or name is not added. It stays in the field with its error, and "Save" stays disabled until it is corrected or cleared. When one part of text with commas is invalid, none of its parts is added.
- **Invalid values from the file**: a value from the file that is invalid shows as a red chip with the error as its tooltip, and "Save" stays disabled until it is removed. Besides the errors above, this is a value of "Types", "Members" or "Tags" that holds a comma, "Contains a comma, which separates values; remove it and add each part on its own.", has whitespace around it, "Has leading or trailing whitespace.", or is listed a second time in the same field, "Listed twice.". Tags count as listed twice in another case as well.
- **"any"**: a selector without values shows "any" below its field and matches every value, so a rule with only "Types" applies to every member of every subject of those types.
- **Every member**: a card whose four selectors are all empty shows "This rule applies to every member.", in red on an allow rule and as a warning on a deny rule. The rule can still be saved, but an allow rule then grants its actions on every member of every subject, as `Everything` does.
- **Deny of one member**: a deny rule with a path that has no `*` and names a property that can hold subjects, such as `/Devices/Kitchen/Lights`, shows "'/Devices/Kitchen/Lights' matches only that one member, not what is below it; add /** to deny everything below it as well.". A deny of `read` on such a property hides the subjects below it from browsing, but their members stay readable when asked for by their paths. A path that names a value, a method or an attribute shows no hint, as nothing is below it, and neither does a path with a `*` anywhere, a path that names an entry of a folder, which has the warning described under "Paths" instead, or a path that names nothing in the current graph.

"Save" stays disabled, and the tab of the rule shows a red mark after its number, while a rule has no action, a value is invalid, or a field holds invalid text. An action in the file that is not one of the four shows as a red chip with the tooltip "Unknown action" and keeps "Save" disabled until it is removed.

### Previewing a Role

The tab "Preview" shows what the role allows on one subject. It uses the role as currently edited, unsaved changes included, together with the roles it includes and the [system rules](#system-rules), as an agent with only this role would get it. It changes nothing.

1. Click the tab "Preview".
2. Type the path of a subject into the field "Subject", such as `/Devices/Bedroom/CeilingLight`, or click the search button next to it, named "Browse Subject" for screen readers, pick the subject in "Select Path" and click "OK".
3. Read the line below the field, whether the subject is readable.
4. Read the table of its members, and hover over or click a cell to see why.
5. Change a rule on the tab "Allow" or "Deny" and return; the preview follows the change.

The preview also follows the roles saved meanwhile, such as an included role saved by another user. A subject added or removed while the dialog is open shows once a rule or a role changes, or the subject is chosen again.

In a window higher than 800 pixels, the field "Subject" and the line below it stay in place, and the table scrolls below its header row. In a lower window, the whole tab scrolls, so that the table can take its height, and the header row of the table stays at the top.

The line below the field reads:

- "Readable: the subject has a readable member and a chain of readable links from the root."
- "Hidden: no member is readable."
- "Hidden: the property Children of /Devices/Cameras is not readable.", naming a property on the way from the root that holds the subject and is not readable, such as for a role that denies `read` on `/Devices/Cameras/**`.
- "Hidden: no chain of parents leads to the root."

A path that names no subject shows "No subject has this path.". A role that does not compile shows no table, as the dialog shows its error above the tabs, such as "Role 'LightOperator' includes 'Readers', which is not defined.".

The table has a row per member: the properties, then the methods, then the attributes, named `Property@Attribute`, such as `Temperature@HomeBlaze:Metadata`. Below its name, a row shows the path a [path pattern](#path-patterns) matches the member by, in a smaller monospace font, such as `/Devices/Sonos/Play`; a click selects the whole path, to copy it into the field "Paths" of a rule. The columns are the [actions](#actions) "read", "write", "execute" and "history". A cell is a green tick when the action is allowed, a red cross when it is denied, and a gray "–" where the action does not apply: a method has "read" and "execute", a property or attribute "read", "write" and "history".

Hovering over a cell shows its explanation, and clicking it shows the explanation below the table, starting with the member and the action. An explanation names the role and the rule that decided:

- "Allowed by Reader: allow any member; actions read, history."
- "Denied by LightOperator: deny paths /Devices/Bedroom/**; types ILightbulb; actions execute. It overrides LightOperator: allow types ILightbulb; members TurnOn, TurnOff, Toggle, SetBrightness; actions execute."
- "Denied by a system rule: deny tags secret; actions read, write, history. It overrides Reader: allow any member; actions read, history."
- "No role allows it."

A method that can be executed but not read cannot be used, so its tick under "execute" is yellow, screen readers announce it as "allowed, cannot be used", and its explanation ends with "It cannot be used, as read is denied: A method is listed and run only when it is readable."

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

A subject is readable for the tools when at least one of its members is readable and the properties that hold it, on the way from the root, are readable. A `deny` of `read` on a branch therefore hides its subjects, for example for an agent that may operate everything but the cameras:

```json
"Household": {
  "includes": [ "Operator" ],
  "deny": [ { "paths": [ "/Devices/Cameras/**" ], "actions": [ "read", "write", "execute", "history" ] } ]
}
```

A role that does not include `Reader` must also allow `read` on the properties that hold subjects along the way, for example with a rule on the tag `subjects`, or everything below the root is hidden.

### Path Patterns

A path pattern is written as the tools build paths, with `/` and `[InlinePaths]` flattened, and matches the paths of members exactly, like a file glob. A member is a property, a method or an attribute, so `/Devices/Sonos/Volume` is one property and `/Devices/Sonos/Play` one method. Only `*` and `**` are wildcards:

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

To cover a subject, end the pattern with `/**`, or with `/*` for its own members only. A pattern that names a subject, such as `/Devices/Kitchen`, matches at most the property that holds it, and nothing when the subject is an entry of a folder, such as a device in `/Devices`; the role's `Warning` points this out. A `deny` of `read` on a property that holds subjects, such as `/Devices/Kitchen/Lights`, hides those subjects and everything below them, since a subject is readable only when the properties that hold it are readable. A pattern starts with `/` and has no empty segment, so a doubled or trailing `/` makes the role fail to compile. A pattern that can match no member, such as `/` or one that ends with an index like `/Line/Machines[CNC1]`, makes the role fail to compile with a hint to add `/**` or `/*`.

An attribute is matched by its property's name followed by `@` and its own name, such as `/Devices/Pump/Temperature@Unit`, as the tools show it under its property, and the metadata of a property by `/Devices/Pump/Temperature@HomeBlaze:Metadata` and that of a subject by `/Devices/Pump/HomeBlaze:Metadata`. These paths do not depend on a value, so a rule on metadata applies to its first write as well. The [library documentation](https://github.com/RicoSuter/Namotion.Interceptor/blob/master/docs/authorization.md#path-patterns) has the complete pattern rules, including subjects with several paths. A deny rule that has only `paths` is checked against every member it matches, so keep its patterns narrow or give it another selector when you can.

---

## Actions

| Action | In the tools |
|--------|--------------|
| `read` | `browse` and `search` list, and `get_property` reads, the property or subject only with `read`. `list_methods` and `$methods` list a method only with `read` on it, `$title` and `$icon` are given only with `read` on the title and the icon when they are members of the subject, such as a derived title, and `invoke_query` and `invoke_operation` report a method without it as "Method not found: ...". Page expressions, `search_markdown` notes, `SearchMetadata` matches and the children `GetOrder` names need it as well |
| `write` | `set_property` writes the property, else "Property is not writable: /path. The access policy does not allow it.". `SetMetadata` needs `write` on the metadata member of its target, else "Metadata of 'path' is not writable. The access policy does not allow it.", and `SetOrder` on the metadata member of its folder, else "Metadata of '/path' is not writable. The access policy does not allow it." |
| `execute` | `invoke_query` and `invoke_operation` run the method, else "Query 'Name' is not permitted. The access policy does not allow it." or "Operation 'Name' is not permitted. ...". The Markdown tools run `ReplaceText`, `ReplaceContent` and `CreateMarkdownFile`, and `search_markdown` searches a page only with `execute` on its `ReadRawContent` |
| `history` | `get_property_history` reads the history of the property, else "History is not available: /path. The access policy does not allow it." A path the roles do not allow to read gives "Path not found: /path" |

A subject the roles hide gives "Subject not found." in the method and Markdown tools, "Path not found: ..." in `get_property`, and "No subject at '...' in this storage." in `SetMetadata`. `GetOrder` of a folder leaves it out, and so do the `order` in the metadata of a folder as `get_property`, `browse`, `search` and page expressions show it and the property `ChildOrder` of a folder, which holds the same order, as page expressions show it, `SearchMetadata` does not find a folder by the name of such a child in its `order`, and an order in `SetOrder` or in the `order` of `SetMetadata` that names it is refused as a name that is no child, and the refusal names only the children the tools list. `list_types`, `update_memory` and the web tools do not touch the graph and are not affected. A denied `execute` or `history` of an agent is logged at debug level with the agent and the member; denied reads and writes are not logged, as the tools ask them for every member they list.

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
- **OPC UA server**: a started OPC UA server is an anonymous read-write gateway over the graph that ignores roles: every OPC UA client that reaches its port reads and writes what the server exposes below its `Path`, whatever the roles of an agent or `/mcp` client hide. Starting it is an `execute` of its operation **Start**, which requires confirmation, so only a person starts it from the HomeBlaze UI. Whoever starts it decides about this access for every OPC UA client.

---

## One Authorization Subject

Only one `Authorization` subject may exist. The create dialog offers the type only while there is none. Agents and `/mcp` clients cannot create an `Authorization` or a `Role`: they create and change only Markdown pages, whose write rules refuse every added block except layout blocks, also with the permission to edit page layout. A second one can still appear, for example from a copied or restored file. Then:

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

- **Default**: `["Operator"]`, for a new agent and for a file without the setting, so `AllowedTools` is the gate of such an agent. Restrict an agent with `Reader` or a custom role.
- **Edit form**: the field "Roles" offers the built-in roles and the custom roles, and each selected role shows as a chip below it. A name that is no role, such as a deleted custom role or the old name of a renamed one, shows as an error chip with the tooltip "Unknown role" that can be removed, and so does a custom role that exists but cannot be used, such as one whose name another role has as well, with the reason as its tooltip. At least one role is required, as an agent without roles is denied everything.
- **Settings tab**: lists the roles, and the reason when the agent is denied everything.
- **When it applies**: each tool call takes the policy of the agent's roles when it starts and decides the whole call with it, so a change of the agent's roles, or of a role with its operations, in its dialog or in `Authorization.json`, applies to the next tool call, also within a running turn and on the MCP route of a CLI agent. The tools a turn offers, its `AllowedTools`, and the "Invalid roles" status are taken when the turn starts.

### Invalid Roles

When the roles cannot be used, the agent shows the status Error with "Invalid roles: " and the reason, such as:

- "No roles are assigned.", for a `Roles` list left empty in the file,
- "Role 'LightOperator' is not defined.", for a name that is no built-in or custom role,
- "A role name is empty.",
- why a custom role does not compile, see [Status and Usage](#status-and-usage),
- "Role '...' is unavailable: ...", for a role with a reserved name or a name with `/`, `[`, `]`, leading or trailing whitespace or an invisible character, or while [several Authorization subjects](#one-authorization-subject) exist.

Turns still run, but every tool call is denied, so the agent can tell you what it cannot do. The status is checked again when the agent starts, when its settings are saved, after each turn and when a role or the `Authorization` subject changes, so it follows a saved role or an edit of `Authorization.json` without waiting for a turn.

---

## External MCP Clients

Clients of `/mcp` use the role `Everything` with the system rules, and `McpServer:ReadOnly` still applies, see [Configuration](configuration.md#mcpserver). They therefore reach every subject but cannot read secrets, write configuration, run operations that require confirmation, reach agents, or read or change the `Authorization` subject and its roles. As for agents, `SetMetadata` refuses a call whose arguments do not have the shape of the method with a description of the arguments, and refuses as a missing one the metadata of a property or subject that the tools do not list. A property that the tools do not list, such as a configuration property without `[State]`, gives "The subject has no property '...'.", and a subject that the tools do not list, such as one of an excluded type, gives "No subject at '...' in this storage." A role per client is not available yet.

---

## Watch Paths

The state a background run receives from the agent's `WatchPaths` is read with the agent's roles, as the tools read it, so the state of agents and whatever else the roles hide stays out. A watch path that the roles hide as a whole appears in the background message with the error of a path that does not exist, which points to a watch path the roles do not cover; below a watched subject, what the roles hide is left out. A change the roles hide starts no background run either, so the timing of runs does not tell when a hidden value changes.

---

## Page Expressions

When an agent or an `/mcp` client reads a page through the tools, a page expression renders only a property that is readable for the tools at its path: the property, every property on its path and every subject on the way readable. Anything else renders empty, as a secret does, and `ReadEvaluatedContent` with `includeSourcePaths` leaves out its path. So an expression below a branch the roles hide, such as `{{ /Devices/Cameras/Door/IsOpen }}` for a role that denies `read` on `/Devices/Cameras/**`, renders empty although `IsOpen` itself is readable. Unlike the tools, an expression may read a member the tools do not list, such as a derived title, when its roles allow it. A value map that an expression applies with `map:` must be readable as well: one the roles hide gives "Cannot resolve map: name", as a missing one does.

For an agent the policy comes from its roles; for `/mcp` clients it is `Everything` with the system rules, so an expression over the state of an agent renders empty for them. The check applies to queries, such as `ReadEvaluatedContent`, which evaluate pages. An operation runs without it, so the change handlers, renders and background work it starts are not restricted by the caller's roles. Pages that people open in the browser are not checked.

## File Format

The `Authorization` subject is stored as JSON, such as `Files/Authorization.json`. The role operations and the edit dialogs of the roles write this file, and it can also be written or changed with any editor; a change of the file applies to the next tool call of each agent. This file holds the role `LightOperator` built in [Editing a Role](#editing-a-role):

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

| Setting | Meaning | In the role dialog |
|---------|---------|--------------------|
| `includes` | The names of built-in or custom roles whose rules this role takes over, transitively | "Includes" on the tab "Roles" |
| `allow` | Rules whose matches are allowed | The tab "Allow" |
| `deny` | Rules whose matches are denied, whichever role allowed them | The tab "Deny" |

- A rule is an object with the arrays `paths`, `types`, `members`, `tags` and `actions`, the fields of a rule card. Only `actions` is required.
- Action names, role names and tags compare ignoring case. Types, member names and path patterns compare exactly.
- An unknown property, such as `path` instead of `paths`, makes the file unreadable rather than widening a rule: it then loads as a plain JSON file without roles, and agents that name its roles get "Role '...' is not defined.".

---

## Known Limits

- **Derived properties**: a rule on a derived property applies to that property, even when it is computed from properties the roles do not allow to read. Deny the derived property as well.
- **Raw page source**: `ReadRawContent`, and so `search_markdown` and the Markdown tools, show the non-secret configuration values of the blocks of a page, even of subjects the roles hide. `ReadEvaluatedContent` evaluates only the expressions in the text and shows the blocks as written as well. Hide such pages from the agent with a `deny` of `read` on their paths.
- **History of removed properties**: `get_property_history` reads only the history of a path that resolves, so the history of a removed device is not available to agents and `/mcp` clients. The history of a resolved property is read under the path it is recorded with, whichever spelling of the path the client used.
- **Subjects with only secrets**: a subject whose only members other than attributes are secrets has no readable member, so it is hidden from every agent and `/mcp` client, as is a subject the roles deny every member of.
- **Page addresses**: `browse` and `search` give a subject with a page its address as `$url`, which is built with the shallowest path of the subject. When a member or subject on that path is hidden, `$url` is left out, so it names nothing hidden, even when the subject is readable for the tools at another path, such as a subject that is also held below a hidden branch.
- **Roles changed while they are read**: when an edit or a reload changes the roles while they are compiled, they are read once more; when that fails as well, custom roles are unavailable for that one call, and the next call reads them again.
- **Metadata checked before a write**: before it decides `SetMetadata` or `SetOrder`, the check adds an empty `HomeBlaze:Metadata` member to the target, also when the write is then refused, so a client without write rights can add such an empty member and raise a structure change. It holds no value and grants nothing.
- **Unknown type names**: a rule may name a type that is not loaded, such as a type of a plugin that is not installed yet, so the role is saved and the name only shows as a warning; until the type is loaded, the rule matches nothing.
- **Role updates from code**: `AuthorizationService.BeginUpdate` and the edit lock of the roles are public, so code that holds an update open keeps every policy as it was when the update started until it ends; the edit dialog and the role operations end their updates themselves.

---

## Related Documentation

- [AI Agents](ai-agents.md) - The agents and their `AllowedTools`
- [Configuration](configuration.md#mcpserver) - The external MCP server
- [Security Design](../architecture/design/security.md) - The design of graph authorization
