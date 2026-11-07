---
title: Authorization
navTitle: Authorization
---

# Authorization

Roles decide what people who sign in, visitors who are not signed in, the built-in [AI agents](ai-agents.md) and the clients of the external `/mcp` endpoint may read, change, run and read the history of. People sign in with a user name and password and hold roles; agents name their roles; `/mcp` uses the roles of the built-in user `mcp`; signed-out visitors use the roles of the built-in user `anonymous`. Fixed system rules apply on top of every role. The design is in [Security Design](../architecture/design/security.md).

On this page:

- [Signing In](#signing-in): the first start, the empty password of `admin`, setup codes and recovery with `--reset-admin`
- [People and Users](#people-and-users): the user operations, [names](#names) and display names, adding and managing users, delegating user management and the roles with deny rules a person passes on
- [Roles and Allowed Tools](#roles-and-allowed-tools): the two gates of an agent
- [Built-in Roles](#built-in-roles): `reader`, `operator` and `everything`
- [Custom Roles](#custom-roles): the `Authorization` subject and adding, renaming and deleting roles with its operations
- [Editing a Role](#editing-a-role): the rule cards and the [preview](#previewing-a-role) of what a role allows
- [Rules and Selectors](#rules-and-selectors): what a rule matches and how rules combine
- [Actions](#actions): `read`, `write`, `execute`, `history` and `administer`
- [Administration](#administration): what needs `administer` and who counts as a full administrator
- [System Rules](#system-rules): the deny rules no role can lift, per kind of principal
- [One Authorization Subject](#one-authorization-subject): what happens with two
- [Agents](#agents): the `Roles` setting, the "Invalid roles" status and who may chat with an agent
- [Anonymous and MCP](#anonymous-and-mcp): the roles of signed-out visitors and of `/mcp` clients, and the `mcp` key
- [Watch Paths](#watch-paths) and [Page Expressions](#page-expressions)
- [Role Recipes](#role-recipes): household member, guest, user manager and a wall tablet
- [Remote Access](#remote-access): the unencrypted HTTP warning and reverse proxy settings
- [Role Settings](#role-settings): `includes`, `allow` and `deny`
- [Known Limits](#known-limits)

---

## Signing In

HomeBlaze shows its pages only to a signed-in person, or to a visitor who is not signed in when the built-in user `anonymous` holds roles, see [Anonymous and MCP](#anonymous-and-mcp). Anybody else gets an empty frame with the product name and the dialog that signs them in, at any address they open: no navigation, page, widget or plugin is rendered, not even in the first response, and they return to the page they opened once signed in. A visitor who is not signed in gets the roles of `anonymous`, which has none by default, so nothing but that dialog is shown until an administrator gives `anonymous` roles; then the app shows and the user menu offers "Sign in". A person who signs in without any role sees "No roles yet. Ask an administrator." instead of an empty application.

Every other address requires a signed-in person as well, or `anonymous` with roles: a request that is not signed in is sent to the start page to sign in, or answered with 401 when it asks for JSON. The exceptions are the sign-in, password, setup and sign-out endpoints the dialogs use, `/health`, `/alive`, static files and `/mcp`, which has rules of its own, see [Anonymous and MCP](#anonymous-and-mcp). Loading the app without being signed in is limited to 60 requests per minute from one client address (an IPv6 client counts per /64 network), which allows about a dozen page loads a minute; a signed-in person is not limited. The limit counts requests, so it relies on WebSockets: with long polling every poll counts, and a client that falls back to it runs into the limit quickly. All visitors who are not signed in and have no client address share one partition.

Users and their roles live in the authentication database, `authentication.db` in the [data folder](configuration.md#data-folder). Set `Authentication:Database` to use another file, see [Configuration](configuration.md#authentication). Passwords are stored as hashes and are never shown.

### First Start

On the first start, with no user other than the built-in ones, HomeBlaze creates the user `admin` with the role `everything` and an empty password:

1. Open HomeBlaze and sign in as `admin` with an empty password.
2. HomeBlaze asks for a new password before anything else, in a dialog that only signing out leaves. A password has at least 10 characters.
3. Add the other people with the operation "Add User" of the `Authorization` subject, see [People and Users](#people-and-users).

The empty password is accepted from any address until a password is set. While `admin` has none, every start logs a warning and full administrators see a banner, because whoever reaches the server first can claim the administrator. Set the password right after the first start, or start HomeBlaze only on a network you trust until you have.

A file `authentication.db.seeded` next to the database file records that this seeding happened. While that marker survives next to the database and the database holds no users, such as after the database was lost or emptied, nothing is seeded: every page shows "Authentication unavailable" with the reason, which points to `--reset-admin`, so that a lost or emptied database never reopens the empty password. The marker belongs to the database file: deleting both, or pointing `Authentication:Database` at a file that does not exist (its marker does not exist either), seeds `admin` with an empty password again, see [Configuration](configuration.md#authentication). See [Recovery](#recovery).

### Signing In and Out

- **Sign in**: the dialog asks for the user name and the password. The user name is lowercased before it is looked up, so `Rico` signs in as `rico`. "Keep me signed in" keeps the session for 14 days, renewed while it is used; otherwise the session ends with the browser. Over plain HTTP the dialog warns that the connection is unencrypted, see [Remote Access](#remote-access).
- **Wrong passwords**: an unknown user and a wrong password give the same message. Five wrong passwords lock the user for five minutes. This lockout only stops guessing: it refuses signing in, setup codes and changing the password, while a session that is signed in already keeps working, including managing users. At most 10 attempts per minute to sign in, set a password or use a setup code are accepted from one client address (an IPv6 client counts per /64 network); more get "Too many attempts. Try again later." Attempts that arrive without a client address share a stricter limit of 3 per minute.
- **Must change the password**: a person who signs in with a setup code that could not store their password, or as `admin` with the empty password, sees only the dialog that sets the password, which only signing out leaves. Until then they may see and do nothing, and every other address answers 403.
- **Account**: the user menu shows who is signed in, with the display name and the user name below it, "Account" and "Sign out". The Account dialog says "Signed in as" the display name followed by the user name, lists the roles of the person, sets their [display name](#display-names) and changes their password, asking for the current one; a wrong current password counts like a wrong sign-in.
- **Sessions end** when a person's password is reset or changed, they are locked or removed. Open pages are signed out at once when the change is made through HomeBlaze, as is every other request of a person who was reset, locked or removed, and every open page is checked again every 5 minutes, so a change made another way, such as by restoring the database, takes up to about 5 minutes to reach them. Changing the roles of a person does not end the session: the open pages switch to the new roles at once, so a removed right does not survive in an open tab either.

### Setup Codes

A new user has no password. Adding a user and resetting a password create a one-time setup code that is valid for 24 hours and shown once, to the person who did it, in the dialog "Setup code for NAME" with a button that copies it and a link to the setup page. The code is kept only by that dialog, so note it before you click "Done". Pass it on to the person in a safe way. They open `/account/setup` of your installation, which shows the setup dialog, and enter the user name, the code and their new password; they are then signed in and the code is spent. A refused password, such as one that is too short, spends no code.

### Recovery

`--reset-admin` creates `admin` when it is missing, sets its roles to `everything` alone, so that no other role, such as one that denies `administer`, keeps it from administering, removes its password so that it signs in with an empty one and must set a new one, unlocks it, ends every session of every user and writes the seed marker. It then exits without starting the server, so it can run while no one can sign in. Stop HomeBlaze first, then run it once:

```bash
dotnet run --project src/HomeBlaze/HomeBlaze -- --reset-admin
```

In Docker, run it as a one-off container on the same data folder:

```bash
docker compose stop homeblaze
docker compose run --rm homeblaze --reset-admin
docker compose up -d
```

The empty password is accepted again until `admin` sets a new one, so sign in at once. There is no setting for this, so it cannot stay switched on by accident.

---

## People and Users

Users are managed with operations in the subject browser. Select the `Authorization` subject: its pane lists the users under "Users", only for people who may administer them, as described in [Administration](#administration), and has the operation "Add User". The pane of a user shows their roles, whether they are locked, whether they still have to set a password, when they last signed in and, for a built-in user, a `Warning` when its roles are a risk, and has the operations below. Each operation runs at once and reports a refusal with its reason.

| Operation | What it does |
|-----------|--------------|
| "Add User" of the `Authorization` subject | Adds a user with a name, see [Names](#names), an optional email address and roles, and shows their [setup code](#setup-codes). A name with uppercase letters is refused, not lowercased; `anonymous` and `mcp` are taken |
| "Set Roles" | Replaces the roles of the user. Open sessions switch to the new roles at once |
| "Set Display Name" | Sets the [display name](#display-names) of the user, or removes it when empty. Their sessions keep going |
| "Reset Password" | Removes the password, ends the sessions of the user and shows a new setup code |
| "Lock", "Unlock" | A locked user cannot sign in and their sessions end |
| "Remove" | Removes the user and ends their sessions |
| "Create Key", "Revoke Key" of `mcp` | Create and remove the key of the external MCP endpoint, see [Anonymous and MCP](#anonymous-and-mcp) |

The field "Roles" of "Add User" and "Set Roles" picks the roles from a list with a chip per selected role. It offers the roles you may assign: every role to a full administrator, and only the roles you hold, counting the roles those include, to anybody else, see [Delegating User Management](#delegating-user-management). When the selection lacks one of your roles with deny rules, the field names it, see [Roles With Deny Rules](#roles-with-deny-rules). The roles are checked again when the operation runs, and a refusal reports the reason. "Add User" starts without roles, and "Set Roles" starts with the roles the user holds now, when you may read them. The operations that remove, reset, lock and unlock ask for confirmation first and say what they do.

The pane of a user disables the operations that do not apply to the user, with the reason as tooltip: "Remove", "Reset Password", "Lock", "Unlock" and "Set Display Name" on the built-in users, and "Create Key" and "Revoke Key" on every user but `mcp`. Passwords, security stamps and setup codes are never part of a user. Every change of a user, role or the `mcp` key is logged without secrets, as are sign-ins, failed sign-ins, lockouts and setup codes, which is the record available until an audit trail exists.

Two rules protect the installation, both counting only full administrators, people with `everything` and no role that denies `administer`, see [Administration](#administration): a user operation that would leave no unlocked full administrator is refused, such as removing or locking the last one, taking `everything` from them or giving them a role that denies `administer`, and so is a change of a role that would leave no unlocked full administrator, such as adding a deny rule on `administer` to a role every one of them holds. Unlocked means not locked by an administrator; a lockout after wrong passwords does not count.

### Names

User names and role names, of the built-in and of custom ones, use lowercase letters `a-z`, digits `0-9`, `.`, `-` and `_`, start with a letter or a digit and have 1 to 64 characters, such as `rico`, `kids-room` or `light.operator`. Another name is refused with "The user name is not valid." or "The role name is not valid." followed by "Use lowercase letters a-z, digits, '.', '-' or '_', starting with a letter or digit, at most 64 characters.". Names compare exactly, so `Reader` is not the role `reader`; an agent or a role that names a name that does not exist reports it as not defined. The built-in roles are `reader`, `operator` and `everything`, the built-in users `anonymous` and `mcp`, and the seeded administrator is `admin`. Only signing in and redeeming a setup code lowercase the typed user name.

### Display Names

A user can have a display name, the name people read in place of the user name, such as their full name, in any script. It has at most 100 characters, spaces around it are removed, and an empty one means none. It contains no control or invisible character, such as a zero-width space or a character that reverses the direction of the text, no line or paragraph separator, no private use character and no character that Unicode does not assign, else "The display name contains a control, invisible, private use or unassigned character.". It needs at least one visible character, so a name of only spaces, marks such as accents, or characters that render blank, such as the Hangul filler, is refused with "The display name has no visible character.", and at most four marks follow each other, else "The display name stacks more than 4 marks on a character.". The user menu and the Account dialog show it with the user name, and the subject browser titles the user with the display name followed by the user name, such as "Rico Suter (rico)", so that two users with the same display name can be told apart. Without a display name they show the user name.

A person sets their own display name in the Account dialog; this needs no `administer` and is limited like the password attempts. People who manage users set it with "Set Display Name", which follows the same rules as the other operations on a user, see [Delegating User Management](#delegating-user-management). Built-in users have none. Setting it ends no session, and open pages show the new name at once.

### Built-in Users

`anonymous` and `mcp` are built in. They have no password and no display name, cannot sign in, be removed, locked or reset, so their panes show these operations disabled, and only a full administrator changes them. See [Anonymous and MCP](#anonymous-and-mcp).

### Delegating User Management

A person without `everything` can manage users when a role gives them `administer` on the users, see [the user manager recipe](#user-manager). Every change they make is also checked:

- they assign and remove only roles they hold, counting the roles those include,
- they cannot change their own roles, and cannot remove, lock or reset themselves,
- they cannot change, remove, lock or reset a user who holds a role they do not hold,
- they never touch the built-in users,
- the user they unlock, reset or give roles, and the roles they assign, hold each of their roles with deny rules; locking, removing and taking roles away are exempt within limits, see [Roles With Deny Rules](#roles-with-deny-rules).

A refused change reports the reason and leaves everything as it was. A delegated user manager therefore cannot hand out more than they have.

### Roles With Deny Rules

Deny rules apply across all roles of a principal: a person holding `kid`, which includes `operator` and denies writing below `/Heating`, cannot write `/Heating`, although `operator` allows it. A user or an agent holding only `operator` is not bound by that deny rule. So whenever a person acts through another principal, that principal's roles, counting the roles those include, must contain each role of the person that has deny rules of its own:

- **Managing users**: the roles a person assigns, when adding a user or changing a user's roles, must include them, and the user they give roles, reset or unlock must already hold them. A `kid` who manages users can assign `operator` together with `kid`, but not `operator` alone, and cannot reset a user holding only `operator`. The refusal names the missing role, such as "The assigned roles must include your roles with deny rules: Kid." or "This user does not hold your roles with deny rules: Kid.". A change that only takes access away is exempt and needs only the usual rules: locking a user, removing a user, changing a user's roles to no roles at all, and changing them so that the new roles, counting the roles they include, are a subset of what the user holds and still include every one of the person's roles with deny rules that the user holds. So the same `kid` can lock, remove or take roles away from a user holding only `operator`, but not reset them, unlock them or give them a role they did not hold through their roles before. From a user holding `kid` and `operator`, they can take `operator` away or take every role away, but not leave `operator` without `kid`, as dropping `kid` would lift its deny rules from the user. A reset is never exempt, as its setup code lets the person sign in as the user.
- **Chatting with an agent**: the agent's roles must include them, see [Who May Chat With an Agent](#who-may-chat-with-an-agent).

This holds for full administrators as well: one who also holds `kid` assigns, manages and chats only along with `kid`, until the role loses its deny rules or the person loses the role through another full administrator. Taking access away stays open to them, such as removing `operator` from `mcp` or `anonymous`. A role that has no rules the application knows, such as one that is not defined or cannot be used, counts as a role with deny rules. A user or an agent without roles may do nothing, so it never needs them. The [system rules](#system-rules) are not roles and do not count.

---

## Roles and Allowed Tools

An agent passes two gates, and a call needs both. People and `/mcp` clients pass only the second:

| Gate | Decides | Set with |
|------|---------|----------|
| `AllowedTools` | Which tools a turn has, such as `invoke_operation` or `set_property` | The agent's `AllowedTools`, see [Tools and permissions](ai-agents.md#tools-and-permissions) |
| Roles | What those tools may reach: which subjects and properties are readable for them, which properties they write, which methods they run and which history they read | The agent's `Roles` |

An agent with `invoke_operation` and only the role `reader` cannot run an operation, and an agent with the role `operator` but without `invoke_operation` cannot either. Roles also hide: a subject or property the roles do not allow to read is treated as if it did not exist.

---

## Built-in Roles

The built-in roles are defined in code, always available and cannot be changed. Their names are reserved. They are listed first below the `Authorization` subject, each with its `Description`, its rules and `UsedByAgents`, and with "Rename" and the Delete button disabled and no "Edit" pencil, see [Adding, Renaming and Deleting Roles](#adding-renaming-and-deleting-roles). The tab "Roles" of the edit dialog of a role describes them below "Includes" as well.

| Role | Allows |
|------|--------|
| `reader` | `read` and `history` on every member; `execute` on `query` |
| `operator` | All `reader` allows, and `write` on every member and `execute` on `operation` |
| `everything` | Every action on every member, including `administer`. A person with it is a full administrator unless another of their roles denies `administer`, see [Administration](#administration) |

The [system rules](#system-rules) apply to all three, so even `everything` never reads a secret and never writes configuration through `write`. For agents and `/mcp` clients it also never runs an operation that requires confirmation, reaches an agent or administers. `reader` and `operator` do not allow `administer`.

- **`reader`** reads every member, also one without a marker, such as a derived title, a property without `[State]` or `[Configuration]` and non-secret configuration. The tools list only state, but such a value reaches a client through page expressions. Read on operations lets `list_methods` list them without allowing them to run.
- **`operator`** allows every write and every operation that the system rules leave, which makes it the default of an agent: `AllowedTools` decides which tools the agent has, and the system rules keep secrets, configuration writes, operations that require confirmation and agents out of reach. For a person it is the household role: read, write state and run operations, but change no configuration.
- The built-in roles are broad on purpose; restrict an agent with a custom role.
- **`everything`** is the role of a full administrator, the seeded `admin` holds it. Give it to people only when they should be able to change everything, see [Administration](#administration). Do not give it to an agent or to `mcp`: the system rules keep these from administering, and an agent is steered by text it reads.

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
| `administration` | An operation that grants power or changes what the application is, such as `Create` of a storage, `Run Now` of an agent, adding or removing a plugin or any operation of `Authorization` and `User`; it also has `operation` |

A member can have several tags, such as a property that is both `[State]` and `[Configuration]`.

---

## Custom Roles

Custom roles are stored in the authentication database, next to the users, and are shown below the one `Authorization` subject, each role as a subject reachable at the path of the `Authorization` subject followed by the role name, such as `/Authorization/light-operator`. Only a full administrator adds, edits, renames and deletes them, in the subject browser: the operation "Add Role" of the `Authorization` subject adds a role, the operation "Rename" of a role renames it, the Delete button of its pane deletes it, and the edit dialog of a role edits its rules. The roles are not part of any file of the data folder, so a backup of the data folder alone does not hold them, see [Installation](installation.md#data-folder). Roles and users are never written by hand.

### The Authorization Subject

HomeBlaze adds the `Authorization` subject as `Files/Authorization.json` to the root storage on the first start, so that the roles and users can be edited. Its file holds nothing but its type. If it was deleted, it is added again at the next start. The dialog "Create" offers the type, in the group "System", only while no `Authorization` subject exists, see [One Authorization Subject](#one-authorization-subject).

### Adding, Renaming and Deleting Roles

Select the `Authorization` subject in the subject browser. Its pane lists the roles under "Roles", the built-in roles first and then the custom roles, and has the operation "Add Role"; the subject has no edit dialog. The pane of a custom role, titled with its name, has the operation "Rename", the "Edit" pencil and the Delete button, and shows the `Status`, `Warning` and `UsedByAgents` of the role, see [Status and Usage](#status-and-usage). The pane of a built-in role shows its rules, its `Description` and `UsedByAgents`, has no "Edit" pencil, and disables the Delete button and "Rename", which says "Built-in roles cannot be edited, renamed or deleted." as tooltip. Only a full administrator runs these operations; others see them disabled, and the Delete button shows only to a full administrator.

To add a role:

1. Click "Add Role" in the pane of the `Authorization` subject. The dialog says what adding a role does to the names in use: "Adds a custom role without rules under the given name. Its rules are edited in its own dialog. Agents and roles that already name it get its rules.", see [Roles That People and Agents Use](#roles-that-people-and-agents-use).
2. Type the name into the field "Name".
3. Click "Execute". The role is saved at once. It has no rules yet, so it allows nothing; open it to add rules, see [Editing a Role](#editing-a-role).

To rename a role:

1. Click "Rename" in its pane. The dialog first says what a rename does: "Gives the role another name and keeps its rules. The roles, users and agents that name it are given the new name as well.", see [Roles That People and Agents Use](#roles-that-people-and-agents-use).
2. Type the new name into the field "Name".
3. Click "Execute" to rename the role, or "Cancel" to keep it. The role keeps its rules and is saved at once.

To delete a role, click the Delete button in its pane. The confirmation asks "Delete 'light-operator'?" with the name of the role and warns "Deletes the role. Refused while users or agents hold it, or while other roles that are in use include it.", see [Roles That People and Agents Use](#roles-that-people-and-agents-use); click "Delete" to delete the role, or "Cancel" to keep it. The role is deleted at once and the pane of the role closes.

The name is checked when "Execute" is clicked. An invalid name changes nothing and shows "Add Role failed with an error." or "Rename failed with an error." with the reason:

- It is not empty, else "Enter a name.". Spaces around it are removed.
- It follows the rule for [names](#names), else "The role name is not valid." with the rule.
- It is not the name of a built-in role, else "The name 'reader' is reserved for a built-in role.".
- No other custom role has it, else "A role named 'garden' already exists.". A rename to the same name changes nothing.

A change is saved in the authentication database before the roles below the `Authorization` subject show it, so a refused change leaves everything as it was. When a rename is saved but the new name cannot be written into the configuration of every agent, "Rename" fails with a message that says the role was renamed and which agents may still name the old name after a restart.

"Rename" and the Delete button of a role that was changed, renamed or deleted after its pane was opened change nothing and fail with "The role is not part of an Authorization subject. It may have been deleted.", as every change of the roles shows them anew. "Add Role" of an `Authorization` subject that was deleted after its pane was opened fails with "The Authorization subject is not part of the graph. It may have been deleted.". Open the subject again in the subject browser.

### Roles That People and Agents Use

Users, agents and the custom roles that include a role name it. A rename keeps them working, a deletion is refused while they use it:

- **Rename**: the role keeps its rules and its assignments. The users, the agents and the roles that include it take over the new name at once, so the old name is free afterwards. Another role or an agent that names a name later, such as a role that is added under the old name, gets the rules of the role of that name.
- **Delete**: the Delete button is refused while a user or an agent uses the role, with "Delete Failed" and "The role 'light-operator' is used by users or agents; remove it from them first."
- **Never a broken principal**: a save, rename or deletion that would make the roles of any user, agent or the built-in users fail to compile, or name a role that does not exist, is refused with the reason, and so is a change that would leave no unlocked user with `everything` and no deny rule on `administer`. One broken role therefore never leaves an administrator without access.
- **Includes**: a custom role that includes a deleted role shows the status "Role '...' includes 'light-operator', which is not defined." and cannot be used until its includes are changed. A deletion that would leave a role in use in that state is refused, as it would break the principals that use that role.

### Status and Usage

The property panel of a role shows these values:

- **`Status`**: why the role cannot be used, or empty. "The name 'reader' is reserved for a built-in role." for the name of a built-in role, where agents naming `reader` keep the built-in role; "The role name is not valid." with the rule for a name that breaks the rule for [names](#names); or why the rules do not compile, such as "Role 'light-operator' includes 'readers', which is not defined.", "Action 'run' of role 'light-operator' is not a known action." or "Path pattern 'Devices' of role 'light-operator' must start with '/'.".
- **`Warning`**: hints about rules that likely do not do what they say, or empty; the role stays usable. "Path pattern '/Devices/Kitchen' names a subject, not a member, and matches nothing; use '/Devices/Kitchen/**' for the subject and everything below it, or '/Devices/Kitchen/*' for its members." for a pattern without a wildcard that names an entry of a folder. The patterns are checked against the graph when the roles change, so a subject that appears later is found at the next change of a role. The edit dialog of the role shows above its tabs the warning the role would have with its unsaved changes; it and the field "Paths" of a rule card check a pattern against the current graph while you edit, see [Rule Cards](#rule-cards).
- **`UsedByAgents`**: the number of started agents that name the role.

Role names compare exactly, see [Names](#names). A saved change applies to the next tool call of each agent, also within a running turn, and to the open pages of people at once, as a whole: nobody gets a part of the changes of one save.

When a role was changed by someone else since you opened its dialog, "Save" does not overwrite their change. It shows "The role 'light-operator' changed since you opened it."; close the dialog, open it again and repeat the change.

---

## Editing a Role

Select the role below the `Authorization` subject in the subject browser, such as `light-operator`, and click "Edit", the pencil next to its title, which only a full administrator sees. The dialog is titled after the role, such as "Edit light-operator". Above its tabs it shows the `Status` the role would have once saved as an error when it could not be used, its `Warning` as a warning, both with its unsaved changes, the hint about execute without read when it applies, see [Rule Cards](#rule-cards), and "Used by N agents". These follow the changes in the dialog, the roles saved meanwhile, such as an included role saved by another user, and the agents that start or stop using the role. The tabs are "Roles", "Allow (n)", "Deny (n)" and "Preview". The numbers count the allow and deny rules, and a red mark after a number says that one of those rules is invalid; a red mark after "Roles" says that "Includes" names a role that cannot be used. The dialog keeps its height, and only the content of the active tab scrolls.

This builds the role `light-operator`, which switches and dims the lights, except in the bedroom, and otherwise only reads:

1. On the tab "Roles", open "Includes" and select `reader`, so the agent still sees and reads everything else.
2. On the tab "Allow", click "Add rule". A new rule card appears.
3. In the field "Types", type `ILightbulb` and pick it from the suggestions.
4. In the field "Members", type `TurnOn, TurnOff, Toggle, SetBrightness` and press Enter. Each name becomes a chip of its own.
5. Under "Actions" of the card, check "execute".
6. On the tab "Deny", click "Add rule", add `/Devices/Bedroom/**` to "Paths" and `ILightbulb` to "Types", and check "execute".
7. Check the result on the tab "Preview", see [Previewing a Role](#previewing-a-role).
8. Click "Save". The role is saved into the authentication database and applies at once.

[Role Settings](#role-settings) lists the settings of a role.

### Rule Cards

The rules are edited on three tabs:

- **"Roles"**: the field "Includes", the roles whose rules this role has as well, built-in or custom. The role itself is not offered. A name that is no role, or a custom role that cannot be used, shows as a red chip with the reason as its tooltip, which removes like any other chip. Below it, under "Built-in roles", `reader`, `operator` and `everything` are described in one line each, with the note that the system rules apply to every role.
- **"Allow"**: a card per rule whose matches are allowed.
- **"Deny"**: a card per rule whose matches are denied, whichever role allowed them.

"Add rule" below each list adds an empty card, and "Remove" on a card removes that rule. While a list is empty, "No allow rules yet" or "No deny rules yet" shows next to "Add rule". A card has a field per [selector](#rules-and-selectors) and the actions:

| Field | Takes |
|-------|-------|
| "Paths" | [Path patterns](#path-patterns) of members, such as `/Devices/Sonos/*` or `/Devices/**/Volume`. The search button next to the field, named "Browse Paths" for screen readers, opens "Select Path" with the tree of the subjects and their members: properties, methods and attributes. A secret can be picked, so that a deny rule can name it, but its value is not shown, nor is the value of a record or object that has a secret property by its declared property types; a secret held in a property typed as `object`, an interface or a base type, in a field or in a tuple is not detected and is shown. Pick one and click "OK" to put its pattern into the field: a subject, or a property that holds subjects, comes with `/**` appended, so that the pattern covers everything below it, and any other member comes with its own path, such as `/Devices/Sonos/Play`. Edit the pattern, such as to end in `/*` for the subject's own members only, and click "Add". A pattern is checked as you type: an invalid one shows its error under the field, such as "Path pattern 'Devices' must start with '/'.", and one that names an entry of a folder shows the warning of the role's `Warning` below the field, see [Status and Usage](#status-and-usage), and a yellow chip once added. |
| "Types" | Type names, case-sensitive. While you type, the field suggests the subject types HomeBlaze knows and the device interfaces they implement, such as `ILightbulb`, by simple name. Any other name can be typed as well, simple or full; a name that matches no known type or interface, such as `ILightBulb`, shows as a yellow chip with the tooltip "No known subject type or interface has this name; check its spelling and case." and can still be saved, for a type of a plugin that is not loaded. A name with whitespace is rejected with "A type name contains no whitespace." |
| "Members" | Member names, case-sensitive, such as `Temperature`; `*` matches any text, such as `Set*`. A name with whitespace is rejected with "A member name contains no whitespace." |
| "Tags" | [Tags](#tags), suggested while you type. Another text shows "Unknown tag; use one of state, configuration, secret, query, operation, requires-confirmation, subjects, attribute, metadata, administration." |
| "Actions" | The checkboxes "read", "write", "execute", "history" and "administer". At least one is required, else the card shows "Select at least one action." |

At their top, the tabs "Allow" and "Deny" say "In each field, press Enter, click Add or leave the field to add the typed text. Types, Members and Tags take several values separated by commas.". Each value is a chip below its field:

- **Adding**: press Enter, click "Add" next to the field or leave the field to add the typed text. "Add" is disabled while the field is empty or holds invalid text. In "Types", "Members" and "Tags", separate several values with commas, such as `TurnOn, TurnOff`; each part becomes a chip, without the whitespace around it. "Paths" takes one path at a time and keeps its commas, as a name in a path can hold one, such as `/Devices/Living room, left/**`. A path with a comma followed by `/` or by a space and `/`, such as pasted `/Devices/A/**,/Devices/B/**`, shows "Looks like several paths separated by a comma, which is part of a path here; add each path on its own." as a warning or, when the path is invalid, after its error. In a field with suggestions, click a suggestion, or choose one with the arrow keys and press Enter. "Save" also adds the text still in a field, so nothing typed is lost.
- **Removing**: click the cross of the chip, or reach it with Tab and press Enter; screen readers name it "Remove" followed by the value. Backspace in an empty field removes its last chip; holding it down removes only that one. Removing the last chip with its cross moves the focus to the field.
- **Invalid text**: an invalid pattern, tag or name is not added. It stays in the field with its error, and "Save" stays disabled until it is corrected or cleared. When one part of text with commas is invalid, none of its parts is added.
- **Invalid values from the file**: a value from the file that is invalid shows as a red chip with the error as its tooltip, and "Save" stays disabled until it is removed. Besides the errors above, this is a value of "Types", "Members" or "Tags" that holds a comma, "Contains a comma, which separates values; remove it and add each part on its own.", has whitespace around it, "Has leading or trailing whitespace.", or is listed a second time in the same field, "Listed twice.". Tags count as listed twice in another case as well.
- **"any"**: a selector without values shows "any" below its field and matches every value, so a rule with only "Types" applies to every member of every subject of those types.
- **Every member**: a card whose four selectors are all empty shows "This rule applies to every member.", in red on an allow rule and as a warning on a deny rule. The rule can still be saved, but an allow rule then grants its actions on every member of every subject, as `everything` does.
- **Deny of one member**: a deny rule with a path that has no `*` and names a property that can hold subjects, such as `/Devices/Kitchen/Lights`, shows "'/Devices/Kitchen/Lights' matches only that one member, not what is below it; add /** to deny everything below it as well.". A deny of `read` on such a property hides the subjects below it from browsing, but their members stay readable when asked for by their paths. A path that names a value, a method or an attribute shows no hint, as nothing is below it, and neither does a path with a `*` anywhere, a path that names an entry of a folder, which has the warning described under "Paths" instead, or a path that names nothing in the current graph.
- **Execute without read**: a method is listed and run only when it is readable. When the role, together with the roles it includes, allows `execute` on methods it does not allow to read, the dialog shows above its tabs "Execute is allowed without read on 2 methods, such as /Devices/Sonos/Play, which therefore cannot be used. A method is listed and run only when it is readable. Allow read on them as well.", and an allow rule of the role itself with "execute" but not "read" that allows them shows a hint of its own; a rule of an included role has none. A role that allows `read` on those methods in another rule, or includes `reader`, shows no hint.

"Save" stays disabled, and the tab of the rule shows a red mark after its number, while a rule has no action, a value is invalid, or a field holds invalid text. An action in the file that is not one of the four shows as a red chip with the tooltip "Unknown action" and keeps "Save" disabled until it is removed.

### Previewing a Role

The tab "Preview" shows what the role allows on one subject. It uses the role as currently edited, unsaved changes included, together with the roles it includes and the [system rules](#system-rules), as an agent with only this role would get it. It changes nothing, and as it lists the members of any subject, only a full administrator sees it.

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

A path that names no subject shows "No subject has this path.", and a role that does not compile shows its error instead, such as "Role 'light-operator' includes 'readers', which is not defined.".

The table has a row per member: the properties, then the methods, then the attributes, named `Property@Attribute`, such as `Temperature@HomeBlaze:Metadata`. Below its name, a row shows the path a [path pattern](#path-patterns) matches the member by, in a smaller monospace font, such as `/Devices/Sonos/Play`; a click selects the whole path, to copy it into the field "Paths" of a rule. The columns are the [actions](#actions) "read", "write", "execute" and "history". A cell is a green tick when the action is allowed, a red cross when it is denied, and a gray "–" where the action does not apply: a method has "read" and "execute", a property or attribute "read", "write" and "history".

Hovering over a cell shows its explanation, and clicking it shows the explanation below the table, starting with the member and the action. An explanation names the role and the rule that decided:

- "Allowed by reader: allow any member; actions read, history."
- "Denied by light-operator: deny paths /Devices/Bedroom/**; types ILightbulb; actions execute. It overrides light-operator: allow types ILightbulb; members TurnOn, TurnOff, Toggle, SetBrightness; actions execute."
- "Denied by a system rule: deny tags secret; actions read, write, history. It overrides reader: allow any member; actions read, history."
- "No role allows it."

A method that can be executed but not read cannot be used, so its tick under "execute" is yellow, screen readers announce it as "allowed, cannot be used", and its explanation ends with "It cannot be used, as read is denied: A method is listed and run only when it is readable."

For the role `light-operator` above and a light in the bedroom, the row `TurnOn` shows a tick under "read" and a cross under "execute". Clicking the cross shows "TurnOn, execute:" followed by the second explanation: the deny rule for the bedroom removes what the allow rule for the lights granted. For a light in the kitchen the same cell is a tick with "Allowed by LightOperator: allow types ILightbulb; members TurnOn, TurnOff, Toggle, SetBrightness; actions execute.".

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
  "includes": [ "operator" ],
  "deny": [ { "paths": [ "/Devices/Cameras/**" ], "actions": [ "read", "write", "execute", "history" ] } ]
}
```

A role that does not include `reader` must also allow `read` on the properties that hold subjects along the way, for example with a rule on the tag `subjects`, or everything below the root is hidden.

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

A role allows one or more of the actions below. In the browser the same actions decide what a person sees and may do, see [Administration](#administration). The column shows what each action means in the tools of agents and `/mcp` clients.

| Action | In the tools |
|--------|--------------|
| `read` | `browse` and `search` list, and `get_property` reads, the property or subject only with `read`. `list_methods` and `$methods` list a method only with `read` on it, `$title` and `$icon` are given only with `read` on the title and the icon when they are members of the subject, such as a derived title, and `invoke_query` and `invoke_operation` report a method without it as "Method not found: ...". Page expressions, `search_markdown` notes, `SearchMetadata` matches and the children `GetOrder` names need it as well |
| `write` | `set_property` writes the property, else "Property is not writable: /path. The access policy does not allow it.". `SetMetadata` needs `write` on the metadata member of its target, else "Metadata of 'path' is not writable. The access policy does not allow it.", and `SetOrder` on the metadata member of its folder, else "Metadata of '/path' is not writable. The access policy does not allow it." |
| `execute` | `invoke_query` and `invoke_operation` run the method, else "Query 'Name' is not permitted. The access policy does not allow it." or "Operation 'Name' is not permitted. ...". The Markdown tools run `ReplaceText` and `ReplaceContent` only with `execute` on them, and `search_markdown` searches a page only with `execute` on its `ReadRawContent` |
| `history` | `get_property_history` reads the history of the property, else "History is not available: /path. The access policy does not allow it." A path the roles do not allow to read gives "Path not found: /path" |
| `administer` | Managing users, and the administration a full administrator does, see [Administration](#administration). Agents and `/mcp` clients are always denied it, whatever their roles say |

A subject the roles hide gives "Subject not found." in the method and Markdown tools, "Path not found: ..." in `get_property`, and "No subject at '...' in this storage." in `SetMetadata`. `GetOrder` of a folder leaves it out, and so does the `order` in the metadata of a folder as `get_property`, `browse`, `search` and page expressions show it, `SearchMetadata` does not find a folder by the name of such a child in its `order`, and an order in `SetOrder` or in the `order` of `SetMetadata` that names it is refused as a name that is no child, and the refusal names only the children the tools list. `list_types`, `update_memory` and the web tools do not touch the graph and are not affected. A denied `execute` or `history` of an agent is logged at debug level with the agent and the member; denied reads and writes are not logged, as the tools ask them for every member they list.

---

## Administration

Changing configuration can run code or point the application at its own data, such as the path of the CLI of an agent, the connection string of a storage or the instructions of an agent. HomeBlaze therefore treats configuration as administration, not as writing: the action `administer` covers it, and a deny rule of the [system rules](#system-rules) keeps `write` and `execute` from reaching it. Operations that grant power or change what the application is carry the tag `administration` and run only for a person who may administer them: `Create` and `Create Markdown File` of storages and folders, `Run Now` of agents, adding and removing plugins, renaming and forgetting clients, and every operation of `Authorization` and `User`.

**Who may administer**

- **Users**: a role that allows `administer` on the users, `Authorization.Users`, `Add User` and `User`, lets a person manage users under the rules of [Delegating User Management](#delegating-user-management).
- **Everything else** needs a full administrator, a person whose roles include `everything` and none of whose roles, counting the roles those include, has a deny rule on `administer`, whatever it selects: changing configuration, creating and deleting subjects, editing pages and files, the other administration operations, role definitions, and the roles and key of `anonymous` and `mcp`. A grant of `administer` on any other member does not make a person an administrator of it; scoped administration of configuration and pages is not available yet.
- **Agents and `/mcp` clients** never administer, whatever their roles say, because what an agent does is steered by text it reads, such as pages and device values.

**What the browser checks**

- **Saving configuration** requires `administer` on every changed member, which for configuration means a full administrator. The same holds for a control on a page, such as a switch, slider or select bound to a configuration property: it is enabled only for a person who may administer that property and is disabled with "Your roles do not allow this." for everyone else, such as an `operator`. A control bound to a property that is no configuration property requires `write`.
- **Creating a subject** in a folder runs its `Create` operation, which requires administration. **Deleting a subject** requires `administer` on the member of its parent that holds it, such as the children of its folder.
- **Editing a Markdown page** requires `administer` on its `Content`, and editing any other file, such as a JSON file, requires `administer` on the member of its folder that holds it, as such a file has no member of its own content. The operations **Replace Content** and **Replace Text** of a page, which the Markdown tools of agents use, are not offered in the browser.
- **Operations** require `execute`; an operation that requires administration requires `administer` instead. An operation that requires confirmation asks first, and a person may run it through its dialog. An operation meant only for agents, such as **Set Metadata** or **Replace Content**, is not offered, and a button configured with its path refuses to run it.
- **Metadata and the order of a folder** require `write` on the metadata member, as for agents.
- **Reading** hides what a person's roles do not allow to read. Navigation, the subject browser, search, the path picker and a page only show subjects the person may read, and a control or widget that is configured with a path the person may not read renders as if the path did not exist. A file of a storage, such as a picture of a page under `/files/`, is served only when the file is shown to the person or visitor, and is otherwise answered with 404, as a missing file is. History charts require `history`.
- **Users** are listed in the subject browser only to people who may administer them: a full administrator, or a person whose roles allow `administer` on the user, provided they may read the `Authorization` subject and its `Users`. They change users only through the user operations in the subject browser. Visitors who are not signed in, agents and `/mcp` clients never see users.
- **Agents**: changing the memory of an agent requires what chatting with it does, see [Who May Chat With an Agent](#who-may-chat-with-an-agent). Creating an agent and changing its settings require a full administrator, so nobody else can give an agent roles.

These checks are made where the application changes something, not in every component. A person may write a secret through a masked input but never read it back. A plugin that assigns a property directly bypasses the checks of the UI: plugin code runs in-process with full trust, and installing plugins requires a full administrator.

---

## System Rules

The system rules are deny rules that apply whatever the roles allow. The kind of principal selects them, and a deny always wins, so no role, `everything` included, lifts them.

| Deny | Actions | Applies to |
|------|---------|------------|
| Members tagged `secret` | `read`, `history` | Everyone |
| Members tagged `configuration` | `write` | Everyone |
| Members tagged `administration` | `execute` | Everyone |
| Members tagged `secret` | `write` as well | Agents and `/mcp` clients |
| Members tagged `requires-confirmation` | `execute` | Agents and `/mcp` clients |
| Every member | `administer` | Agents, `/mcp` clients and visitors who are not signed in |
| Agents (`LlmAgentBase` and every type derived from it) | All actions | Agents and `/mcp` clients |
| `Authorization`, `Role` and `User` | All actions | Agents and `/mcp` clients |
| `Authorization` and `Role` | `write`, `execute` | People and visitors who are not signed in |
| `User` | `read`, `write`, `execute`, `history` | People and visitors who are not signed in |

- **Agents and `/mcp` clients** are non-human: a secret would leak, a configuration write could change an agent's `AllowedTools` or a device's address, a confirmation would be bypassed, and an agent that reaches agents, roles or users could raise its own permissions.
- **People** may run an operation that requires confirmation through its dialog and write a secret through a masked input, but never read a secret. Roles, the `Authorization` subject and users change only through their own operations and dialogs, never through `write` or `execute`, and the details of users are shown in the subject browser only to people who may administer them.
- **Visitors who are not signed in** get the rules of people and may never administer, so `anonymous` can never be given administration.

- **Confirmation first**: for an agent or `/mcp` client, an operation that requires confirmation is refused with "'Name' requires confirmation by a person and cannot be called through MCP; run it from the HomeBlaze UI.", which names the actual reason. `list_methods` marks such methods with `requiresConfirmation`.
- **Effects of operations**: the rules decide about members, not about what an operation does. An operation may still change configuration as part of its work, such as **Set Speed** of the sample motor setting `TargetSpeed`, so grant `execute` on operations as carefully as `invoke_operation`. The writes an operation makes are not checked again.
- **OPC UA server**: a started OPC UA server is an anonymous read-write gateway over the graph that ignores roles: every OPC UA client that reaches its port reads and writes what the server exposes below its `Path`, whatever the roles of a person, an agent or an `/mcp` client hide. Starting it needs only `execute` on its operation **Start**, no `administer`; as **Start** requires confirmation, only a person starts it, from the HomeBlaze UI. Whoever starts it decides about this access for every OPC UA client.

---

## One Authorization Subject

Only one `Authorization` subject may exist. The create dialog offers the type only while there is none. Agents and `/mcp` clients cannot create an `Authorization` or a `Role`: they create no subjects or files, as that is administration, and change only Markdown pages, whose write rules refuse every added block except layout blocks, also with the permission to edit page layout. A second one can still appear: a full administrator can add one as a block of a page or as a file, and a copied or restored file can bring one. Then:

- neither is used, and each, with each of its roles, shows the status "Only one Authorization subject is allowed; remove the others. Custom roles are unavailable until then.",
- the built-in roles keep working,
- users cannot be added or changed, as the user operations find no single `Authorization` subject and refuse,
- an agent that names a custom role is denied everything with "Invalid roles: Role 'light-operator' is unavailable: Only one Authorization subject is allowed; ...", until one of them is removed.

---

## Agents

Each agent has the setting `Roles`, a list of role names:

```json
{
  "$type": "HomeBlaze.AI.Agents.ClaudeCli.ClaudeCliAgent",
  "name": "Light Assistant",
  "roles": ["light-operator"],
  "allowedTools": ["browse", "search", "get_property", "list_methods", "invoke_query", "invoke_operation"]
}
```

- **Default**: `["operator"]`, for a new agent and for a file without the setting, so `AllowedTools` is the gate of such an agent. Restrict an agent with `reader` or a custom role.
- **Edit form**: the field "Roles" offers the built-in roles and the custom roles, and each selected role shows as a chip below it. A name that is no role, such as a deleted custom role or the old name of a renamed one, shows as an error chip with the tooltip "Unknown role" that can be removed, and so does a custom role that exists but cannot be used, such as one whose name another role has as well, with the reason as its tooltip. At least one role is required, as an agent without roles is denied everything.
- **Settings tab**: lists the roles, and the reason when the agent is denied everything.
- **Who may change it**: creating an agent and changing its settings, including its roles, require a full administrator, see [Administration](#administration).
- **When it applies**: each tool call takes the policy of the agent's roles when it starts and decides the whole call with it, so a change of the agent's roles, or of a role with its operations or in its dialog, applies to the next tool call, also within a running turn and on the MCP route of a CLI agent. The tools a turn offers, its `AllowedTools`, and the "Invalid roles" status are taken when the turn starts.

### Invalid Roles

When the roles cannot be used, the agent shows the status Error with "Invalid roles: " and the reason, such as:

- "No roles are assigned.", for a `Roles` list left empty in the file,
- "Role 'light-operator' is not defined.", for a name that is no built-in or custom role,
- "A role name is empty.",
- why a custom role does not compile, see [Status and Usage](#status-and-usage),
- "Role '...' is unavailable: ...", for a role with a reserved name or a name with `/`, `[`, `]`, leading or trailing whitespace or an invisible character, or while [several Authorization subjects](#one-authorization-subject) exist.

Turns still run, but every tool call is denied, so the agent can tell you what it cannot do. The status is checked again when the agent starts, when its settings are saved, after each turn and when a role or the `Authorization` subject changes, so it follows a saved role without waiting for a turn.

### Who May Chat With an Agent

An agent has one conversation, timeline and memory, shared by everyone who chats with it, and its background runs feed their findings into that conversation. A person may therefore chat with an agent, read its timeline, activity, memory and `Last Analysis`, start a new conversation and run it now, only when they hold every role of the agent, counting the roles those include, or are a full administrator, and the agent's roles include each of the person's roles with deny rules, see [Roles With Deny Rules](#roles-with-deny-rules). Everything the agent can see or do is then something every person who may chat with it may see or do, so shared history, findings and memory cannot leak or escalate between people. The agent page shows why it is refused, such as "You can chat with this agent only if you hold its roles. Missing: operator." or "You can chat with this agent only if it holds your roles with deny rules, so that it cannot do what they deny you. Missing: kid." An agent without roles can be used only by a full administrator. Each tool call of a turn uses the agent's roles at that call, see [Agents](#agents).

A change of the agent's roles applies to its open pages at once: a person who may no longer act through the new roles sees the reason in place of the chat, the activity and the memory. The conversation, the timeline, the memory and the session of the LLM are kept, so what the agent saw and wrote under earlier roles stays visible to everyone who may act through its current roles, and the agent can repeat it in the chat. Before you narrow the roles of an agent that saw more than the people who will chat with it may see, start a new conversation, which discards the session, and remove the memory entries that must not reach them; the timeline keeps the earlier runs and chats.

---

## Anonymous and MCP

The two built-in users are principals without a password. Only a full administrator changes their roles, with the operation "Set Roles" of the user.

| User | Is the principal of | Default roles |
|------|---------------------|---------------|
| `anonymous` | Visitors who are not signed in | None |
| `mcp` | The clients of the external `/mcp` endpoint | `reader` |

**`anonymous`** decides what anyone who can reach HomeBlaze sees without signing in. With no roles, signing in is required. Give `anonymous` roles only on a network you trust: they apply to everyone who can reach the server, and its `Warning` says so while it has roles. `anonymous` can never administer. See [the wall tablet recipe](#wall-tablet).

**`mcp`** is the principal of `/mcp`, which is off unless `McpServer:Enabled` is true, see [Configuration](configuration.md#mcpserver). Its roles decide what clients may read, write and run, with the system rules for agents and `/mcp` clients: they cannot read secrets, write configuration, run operations that require confirmation, reach agents, or read or change the `Authorization` subject, roles and users. The default `reader` keeps `/mcp` read-only, and there is no separate read-only switch; give `mcp` `operator` to let clients write and run operations. All clients share the roles of `mcp`; a role per client is not available yet.

- **Key**: without a key, `/mcp` is unauthenticated, so anyone who can reach it gets the roles of `mcp`. A key makes `/mcp` require it as a bearer token, `Authorization: Bearer <key>`, and answer 401 without it. The operations `Create Key` and `Revoke Key` of the user `mcp` create and remove it; the key is shown once in a dialog when it is created, replaces an earlier one and is stored only as a hash. Its `Warning` says so while `mcp` holds more than `reader` and has no key. Give the key to the client and keep it as secret as a password. Create a key whenever other devices than the server can reach `/mcp`.
- **Origin**: `/mcp` answers 403 to a request whose `Origin` header names another scheme, host or port than the request, so that a web page in a browser cannot use it with the roles of `mcp`; MCP clients that do not run in a browser send no `Origin` header and pass, so a client that runs in a browser must connect from the same origin or through a proxy.
- **Metadata**: as for agents, `SetMetadata` refuses a call whose arguments do not have the shape of the method with a description of the arguments, and the metadata of a property or subject that the tools do not list as a missing one. A property that the tools do not list, such as a configuration property without `[State]`, gives "The subject has no property '...'.", and a subject that the tools do not list, such as one of an excluded type, gives "No subject at '...' in this storage."

---

## Watch Paths

The state a background run receives from the agent's `WatchPaths` is read with the agent's roles and the system rules, as the tools read it, so a value the roles may not read, and the state of agents, stay out of the message. A watch path that the roles hide as a whole appears in the background message with the error of a path that does not exist, which points to a watch path the roles do not cover; below a watched subject, what the roles hide is left out. A change the roles hide starts no background run either, so the timing of runs does not tell when a hidden value changes. Because the agent reports into a conversation that every person who holds its roles can read, see [Who May Chat With an Agent](#who-may-chat-with-an-agent), watch only what these people may see.

---

## Page Expressions

When an agent or an `/mcp` client reads a page through the tools, a page expression renders only a property that is readable for the tools at its path: the property, every property on its path and every subject on the way readable. Anything else renders empty, as a secret does, and `ReadEvaluatedContent` with `includeSourcePaths` leaves out its path. So an expression below a branch the roles hide, such as `{{ /Devices/Cameras/Door/IsOpen }}` for a role that denies `read` on `/Devices/Cameras/**`, renders empty although `IsOpen` itself is readable. Unlike the tools, an expression may read a member the tools do not list, such as a derived title, when its roles allow it. A value map that an expression applies with `map:` must be readable as well: one the roles hide gives "Cannot resolve map: name", as a missing one does.

For an agent the policy comes from its roles; for `/mcp` clients it comes from the roles of `mcp`. The check applies to queries, such as `ReadEvaluatedContent`, which evaluate pages: they take the permissions of the reader. An operation runs with permissions that deny everything, so the change handlers, renders and background work it starts are not restricted by the caller's roles.

In the browser the same rule holds for each viewer: a page expression, and a value widget bound by path, shows its value only when the viewer may read every property it read and is shown the subject of each, so a branch the roles hide shows none of its values; it renders empty otherwise. There is no general read check in the application; page methods take the reader's permissions, and a missing reader means nothing may be read.

---

## Role Recipes

Roles are assigned with the operations "Add User" and "Set Roles", see [People and Users](#people-and-users). These recipes use the built-in roles and a custom role; build custom roles as in [Editing a Role](#editing-a-role).

### Household Member

Give the person the built-in role `operator`. They read everything, switch and set values, and run operations, but change no configuration, pages or subjects: the system rules and [administration](#administration) keep configuration for full administrators. For a person who only looks, use `reader`.

### Guest

Create a custom role that allows what a guest needs and nothing else. For example, `guest` reads one page and its devices and runs the light operations:

1. Do not include `reader`. A role that does not include it needs `read` on the properties that hold subjects along the way, so add an allow rule with the tag `subjects` and the action `read`.
2. Add an allow rule with the paths `/Guest/**`, a folder that holds the pages of the guest, and `/Devices/LivingRoom/**` and the action `read`.
3. Add an allow rule with the types `ILightbulb`, the members `TurnOn, TurnOff, Toggle` and the action `execute`, plus `read` so that the methods are listed.
4. Check the result on the tab "Preview" with a subject of each branch.

A guest then sees only what these rules allow. Mind the [known limits](#known-limits): a read deny hides a member path, it does not stop data that flows through a derived property or the source of a page.

### User Manager

A person who adds and manages the users of a household without holding `everything`: create a custom role `user-manager` with two allow rules for `administer`, one with the paths `/Authorization/Users/**` and one with the path `/Authorization/AddUser`, and include `reader` so the person can read the `Authorization` subject and its `Users`. A user manager assigns only roles they hold, so give them the roles they should hand out as well, and the delegation rules of [Delegating User Management](#delegating-user-management) apply. They never change their own roles, the built-in users, or a user holding a role they do not hold.

### Wall Tablet

A tablet on the wall that shows a dashboard without anyone signing in: give `anonymous` the role `reader`, or a custom role that reads only the dashboard, and use `operator` when the tablet should also switch devices. Everyone who can reach HomeBlaze then gets these rights without signing in. With `operator` that includes operations that require confirmation, such as unlocking a door: the system rules for people and visitors do not deny `requires-confirmation`, so anyone who reaches the server can run them through their confirmation dialog. For a tablet that switches devices, create a custom role that includes `operator` and has a deny rule with the tag `requires-confirmation` and the action `execute`, or restrict the role to the paths the tablet needs. Do this only on a network that only trusted devices reach, never on one that is reachable from the internet, and not behind a reverse proxy that is open to the internet. The alternative is a user for the tablet, signed in once with "Keep me signed in", which lasts 14 days and renews while it is used.

---

## Remote Access

- **HTTPS**: the session cookie is marked secure only when the server sees the request as HTTPS. When the address in the browser is not HTTPS, or the server did not see the request as HTTPS, for example behind a TLS proxy that is not trusted, the sign-in, set-password and setup dialogs warn "This connection is unencrypted HTTP: anyone on the network can read the password and take over the session."; the running application shows no such banner. Use HTTPS or a reverse proxy that terminates TLS for anything but a trusted network.
- **Reverse proxy**: a proxy that terminates TLS must tell HomeBlaze the original scheme, host and client address, or the cookie is not secure, the warning appears wrongly and the sign-in limit and the limit of loading the app count the proxy as one client. HomeBlaze trusts forwarded headers only from the proxies you configure: list their networks in CIDR notation in `Authentication:ForwardedHeadersKnownNetworks`, such as `["172.18.0.0/16"]`, or their addresses in `Authentication:ForwardedHeadersKnownProxies`, see [Configuration](configuration.md#authentication). Without either, no forwarded header is applied, because any client could send one to spoof its address. The proxy must support WebSockets.
- **Base path**: when the proxy serves HomeBlaze below a path, such as `/homeblaze`, set `Authentication:PathBase` to it.
- **Origin check**: the live connection of the browser is refused when its `Origin` header names another scheme, host or port than the request, as is a request to `/mcp`, see [Anonymous and MCP](#anonymous-and-mcp), so the proxy must pass the original `Host` header and the scheme. A request without an `Origin` header is let through.
- **Authenticating proxies**: HomeBlaze has its own sign-in; a proxy that also authenticates adds a second layer but does not replace the roles.

---

## Role Settings

A role has these settings, each optional. Roles are added, renamed and deleted with operations, edited in the dialog of each role and stored in the authentication database, see [Custom Roles](#custom-roles):

| Setting | Meaning | In the role dialog |
|---------|---------|--------------------|
| `includes` | The names of built-in or custom roles whose rules this role takes over, transitively | "Includes" on the tab "Roles" |
| `allow` | Rules whose matches are allowed | The tab "Allow" |
| `deny` | Rules whose matches are denied, whichever role allowed them | The tab "Deny" |

- A rule has the lists `paths`, `types`, `members`, `tags` and `actions`, the fields of a rule card. Only `actions` is required.
- Action names and tags compare ignoring case. Role names, types, member names and path patterns compare exactly.
- The role `light-operator` built in [Editing a Role](#editing-a-role) includes `reader`, allows `execute` on `TurnOn`, `TurnOff`, `Toggle` and `SetBrightness` of every `ILightbulb`, such as a Philips Hue light, and denies `execute` for the lights at and below `/Devices/Bedroom`. A light that does not implement `IBrightnessController` has no `SetBrightness`, so the member does not match there. Give an agent this role with `"roles": ["light-operator"]` in its settings and allow `invoke_operation`, see [Agents](#agents); the role allows no `write`, so the agent cannot write a property even with `set_property` allowed.

---

## Known Limits

- **Derived properties**: a rule on a derived property applies to that property, even when it is computed from properties the roles do not allow to read. Deny the derived property as well.
- **Raw page source**: `ReadRawContent`, and so `search_markdown` and the Markdown tools, show the non-secret configuration values of the blocks of a page, even of subjects the roles hide. `ReadEvaluatedContent` evaluates only the expressions in the text and shows the blocks as written as well. Hide such pages from the agent with a `deny` of `read` on their paths.
- **History of removed properties**: `get_property_history` reads only the history of a path that resolves, so the history of a removed device is not available to agents and `/mcp` clients. The history of a resolved property is read under the path it is recorded with, whichever spelling of the path the client used.
- **Subjects with only secrets**: a subject whose only members other than attributes are secrets has no readable member, so it is hidden from every agent and `/mcp` client, as is a subject the roles deny every member of.
- **Page addresses**: `browse` and `search` give a subject with a page its address as `$url`, which is built with the shallowest path of the subject. When a member or subject on that path is hidden, `$url` is left out, so it names nothing hidden, even when the subject is readable for the tools at another path, such as a subject that is also held below a hidden branch.
- **Roles changed while they are read**: when an edit or a reload changes the roles while they are compiled, they are read once more; when that fails as well, custom roles are unavailable for that one call, and the next call reads them again.
- **Watch paths**: see [Watch Paths](#watch-paths).
- **Read denies apply to member paths, not to where data flows**: a query, a derived property or the raw source of a page can show data of a member the viewer may not read. Inside a widget whose subject is shown, reading is decided per subject. Hide such data with a deny on every path it flows through, not on one.
- **Plugins**: a plugin widget that assigns a property directly bypasses the checks of the UI, see [Administration](#administration).
- **Gaps in collection indexes**: a hidden item of a collection is left out where the item is listed, such as in the subject browser, the path picker and a table, but the other items keep their indexes. A viewer can therefore tell from a gap, such as `[0]` and `[2]` without `[1]`, that an item is hidden, though not what it is.
- **Agents change page text for the people who chat with them**: people change the text of a page only in the page editor, which requires a full administrator, but the Markdown tools of agents run `ReplaceText` and `ReplaceContent` with `execute` on them, see [Changing pages](ai-agents.md#changing-pages). A person who may chat with an agent, such as an `operator` person with an `operator` agent, can therefore ask it to change the text of any page its roles allow. Give agents that people chat with roles without `execute` on these operations of the pages they must not change, or leave `edit_markdown`, `write_markdown` and `invoke_operation` out of their `AllowedTools`, or give the agent roles that only the people who may change these pages hold.
- **Empty password of `admin`**: on the first start, and after `--reset-admin`, `admin` signs in with an empty password from any address until a password is set, see [First Start](#first-start). Start HomeBlaze only on a network you trust, or keep it unreachable from other devices, until the password is set, and set it right away.
- **Signing out ends one session**: "Sign out" ends the session of that browser only. Other browsers and devices stay signed in, and a copied session cookie stays valid for up to 14 days. To end every session of a person, change the password, which ends the other sessions, or have an administrator reset or lock the user.
- **Timing of refused sign-ins**: every refused sign-in verifies one password hash, but a wrong password for an existing user also stores the failed attempt, which takes a few milliseconds more than a name that matches no user. Someone who measures many attempts may therefore tell whether a user name exists; the rate limit of 10 attempts per minute makes this slow. Do not treat user names as secret.
- **Origin check of `/mcp`**: the check refuses a page of another origin in a browser, but a page that makes its host name point to the server (DNS rebinding) sends a matching `Origin`, so without a key it can use `/mcp` with the roles of `mcp` through a browser on your network. Create a key for `mcp` whenever `/mcp` is enabled and other devices can reach the server.
- **The tool list of `/mcp`**: every tool appears in the list of `/mcp`, also one whose calls the roles of `mcp` refuse; such a call fails with a message that says why. Tell clients which tools the roles allow.
- **People and agents resolve paths under different rules**: a person sees a member when the roles allow reading it and its subject is shown, that is, some chain of readable parents leads to a root; an agent or `/mcp` client needs every member and subject on the path from the root of the tool readable. The same roles can therefore show a value to a person that an agent with these roles cannot reach at the path it uses. Write roles with rules on the branches, not only on single members, so that both rules agree.
- **Addresses through hidden folders**: a subject that is held in more than one place has one address, through the first place, which navigation, links and the address bar show also to a viewer who may see the subject only through another place. The name of a hidden folder can therefore appear in such an address. Keep a page in one folder only, or name folders so that their names reveal nothing.
- **Operations are decided by the operation, not by what they change**: running an operation needs `execute` on it, or `administer` when it requires administration, also when it writes configuration or other members as part of its work, which the roles may not allow writing directly. Deny `execute` on such operations for roles that must not cause these changes.
- **Edit components of subjects without configuration**: the configuration dialog of a subject without configuration members opens for every viewer who sees the subject, and its edit component must check each member it changes itself. The built-in ones do; a plugin edit component that does not lets anyone who sees the subject change it, so a plugin is trusted code, as for widgets.
- **Metadata checked before a write**: before it decides `SetMetadata` or `SetOrder`, the check adds an empty `HomeBlaze:Metadata` member to the target, also when the write is then refused, so a client without write rights can add such an empty member and raise a structure change. It holds no value and grants nothing.
- **Unknown type names**: a rule may name a type that is not loaded, such as a type of a plugin that is not installed yet, so the role is saved and the name only shows as a warning; until the type is loaded, the rule matches nothing.

---

## Related Documentation

- [AI Agents](ai-agents.md) - The agents and their `AllowedTools`
- [Installation](installation.md) - First start, backups and upgrading
- [Configuration](configuration.md#authentication) - The `Authentication` settings and the external MCP server
- [Security Design](../architecture/design/security.md) - The design of graph authorization
