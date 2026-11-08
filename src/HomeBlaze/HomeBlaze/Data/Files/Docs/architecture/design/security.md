---
title: Security
navTitle: Security
status: In Progress
---

# Security Design

## Overview

Security in HomeBlaze covers sign-in, graph-level authorization (who can read and write which properties, invoke which methods, read which history and administer), inter-node authentication, and MCP access control. Sign-in and authorization are implemented for people in the browser, signed-out visitors, the built-in AI agents and the external `/mcp` endpoint; inter-node authentication, per-user tokens for MCP clients and scripts, and an audit trail of graph writes are planned. The operator guide is [Authorization](../../administration/authorization.md).

## Graph Authorization [Implemented]

Authorization is role-based, with roles as data. The generic engine is the library `Namotion.Interceptor.Authorization`, see its [documentation](../../../../../../../../docs/authorization.md) and [design](../../../../../../../../docs/design/authorization.md): rules select members of the graph by path, type, member name and tag, roles hold allow and deny rules and include other roles, and `AuthorizationDefinition.Create` compiles the rules once and `Compile` turns the roles of one principal into an immutable policy. `HomeBlaze.Authorization` supplies what the library leaves to the application: the roles, the tags, the actions and the system rules.

| Part | Implementation |
|------|----------------|
| Actions | `read`, `write` (built into the library), `execute`, `history` and `administer` (`AuthorizationActions`) |
| Tags | `HomeBlazeMemberTagProvider` tags members from the existing markers: `state`, `configuration`, `secret`, `query`, `operation`, `requires-confirmation`, `administration`, `subjects`, `attribute`, `metadata`. No subject type needs an attribute of its own. The tags of a member are cached until an attribute is added to it, and only a property of type `MethodMetadata` is read |
| Built-in roles | `reader` (read and history on every member, execute on queries), `operator` (`reader`, write on every member, execute on operations) and `everything` (every action, including `administer`) in code (`BuiltInRoles`), always present, not editable, names reserved. They are broad: `operator` allows every write and operation the system rules leave, so `AllowedTools` decides what an agent does, and it is what a household member needs; restriction is the job of custom roles. A person whose roles include `everything` is a full administrator unless one of their roles denies `administer` |
| Custom roles | Rows of the authentication database, projected into the `Roles` dictionary of the single `Authorization` subject as `Role` subjects with `Includes`, `Allow` and `Deny`, after a read-only `Role` per built-in role (`IsBuiltIn`), which the policy never reads from the graph. The projection is read-only and never saved to JSON; roles change only through the role store |
| System rules | `SystemRules`, compiled into every policy as the library's system deny rules |
| Policies | `AuthorizationService.GetPolicy(roleNames, kind)` |
| Enforcement | `AuthorizationMcpAccessPolicy`, the `IMcpAccessPolicy` of the MCP tools, and the `IUserPermissions` of the person of a circuit, which is also what a method takes for page expressions |

### System Rules

`SystemRules.For(PrincipalKind)` gives the deny rules compiled into every policy of a kind of principal. No role lifts them, because a deny always wins.

```text
Every kind:
deny  [secret]                         read, history
deny  [configuration]                  write
deny  [administration]                 execute

Non-human (agents and /mcp) additionally:
deny  [secret]                         write
deny  [requires-confirmation]          execute
deny  (every member)                   administer
deny  types: LlmAgentBase              read, write, execute, history, administer
deny  types: Authorization, Role, User read, write, execute, history, administer

People additionally:
deny  types: Authorization, Role       write, execute
deny  types: User                      read, write, execute, history

Visitors who are not signed in: the people rules, and
deny  (every member)                   administer
```

`LlmAgentBase` is named by its full type name, since `HomeBlaze.Authorization` does not reference `HomeBlaze.AI`. `McpMethodInvoker` has its own refusal of an operation that requires confirmation, checked before the policy, so the client is told the actual reason. `[ExcludeFrom]` stays separate: it decides what is offered, not who may reach it.

Configuration is administration, not writing: changing it can run code or point the application at its own data. `[Operation(RequiresAdministration = true)]` marks the operations that grant power or change what the application is, and the tag `administration` carries it. The system rules keep `write` from reaching configuration and `execute` from reaching these operations, so the generic property panel, `PropertyWriter` and `OperationInvoker` never change configuration through them. Administration is decided where something changes: `IUserPermissions.CanAdminister(member)` allows `administer` on a user-management member when the policy allows it, and on any other member only for a full administrator. Scoped administration of configuration and pages needs a per-type safety classification and is a later step.

The generic editor of a subject, its Edit button (`EditPermissions.CanEdit`) and the save of `SubjectEditPanel`, needs at least one configuration member and `administer` on every one of them (`UserPermissionsExtensions.CanAdministerConfiguration`). A file follows the rule of its content (`EditPermissions.CanAdministerFileContent`). A subject whose edit component changes other members, such as a role, has an operation with `Purpose = OperationPurpose.Edit`: the browser shows the Edit button to a viewer who may invoke it (`CanInvokeEditOperation`), and the component saves by invoking it through `UserPermissionsExtensions.TryInvokeAsync`, so the save is decided as any operation.

### Policies

`AuthorizationService` finds the `Authorization` subjects in the registry and compiles the built-in roles, the custom roles of the single `Authorization` subject, the system rules, the five actions, the tag provider and the path provider `RulePathProvider`. It builds the paths of the MCP tools (`StateAttributePathProvider`), so that a path pattern matches the paths the tools build, and names every other attribute by the name of its property, `@` and its own name, such as `Temperature@Unit` also for an attribute declared in C# as `Temperature_Unit`, and every metadata member, such as `Temperature@HomeBlaze:Metadata`, with or without a value, as the tools list metadata only while it has one and a rule must decide the first write of metadata as it decides a later one.

- **Cache**: one policy per distinct set of role names, ignoring order. A change of an `Authorization` or a `Role`, observed through property changes and the lifecycle, builds a new snapshot; a policy once handed out never changes. After a rebuild, `PoliciesChanged` is raised on the thread pool, on which started agents check their roles again. The service is thread-safe.
- **Concurrent edits**: when the graph or a role list changes while a snapshot is built, its enumeration throws; the snapshot is built once more, and when that fails as well, a stale snapshot of the built-in roles makes custom roles unavailable for that call, so the error never reaches an agent turn or `ApplyConfigurationAsync`.
- **Errors**: no name at all, an empty or unknown name, a role that does not compile, a custom role with a reserved or duplicate name, or a custom role while several `Authorization` subjects exist gives `AuthorizationPolicy.DenyAll` with the reason. A policy never partially applies.
- **Statuses**: each `Role` shows why it cannot be used, from compiling it on its own, a `Warning`, for a role that compiles, when a path pattern without a wildcard resolves in the graph to an entry of a folder or collection and so matches no member (the role stays usable, because the result depends on the graph at the time of the check), and how many started agents name it (`UsedByAgents`); each `Authorization` shows when it is one of several. Statuses are written in the background after a change. `GetUnavailableRoles` lists the custom roles that exist but cannot be used with the reason, which the role picker shows.
- **Single instance**: `Authorization` is `[SingleInstance]`. A second one (a copied or restored file) disables all custom roles until it is removed, while the built-in roles keep working, so two sets of roles are never merged silently.
- **No creation by clients**: an agent or `/mcp` client cannot create an `Authorization` or a `Role`, which would disable the custom roles or define roles that principals already name. Creating a subject or a file, a Markdown page included, is administration, which the system rules deny them, and the [page write rules](ai.md#page-write-rules) of the pages they change refuse every added block that is no layout block (`[PageLayout]`), whatever its type and however deeply it is nested; metadata files are never loaded as subjects and refuse keys that start with `$`; the create dialog of a folder lets a person pick the type; adding a plugin and starting an OPC UA client require confirmation.

### Principals

| Principal | Policy |
|-----------|--------|
| Built-in agent | Its `Roles`, default `["operator"]`, which allows every write and operation the system rules leave, so `AllowedTools` is the gate of an agent with the default. A chat turn and a background run take `AllowedTools` once; each tool call, also one through the MCP route of a CLI agent, fetches the policy of the current `Roles` (`AuthorizationMcpAccessPolicy.BeginCall`, cached per compiled snapshot) and decides the whole call with it, so a removed role is refused at the next call while a call stays consistent. Roles that yield `DenyAll`, also an empty list, show "Invalid roles: ..." in the agent's status, and the turn runs with tools that deny everything |
| Watch paths of an agent | The agent's `Roles` with the system rules, read when a background run starts: a watched value the roles may not read is left out of the state a run receives, and a watched change starts a run only when the roles allow to read the property and show its subject |
| External `/mcp` | The roles of the built-in user `mcp`, default `reader`, with the non-human system rules. An optional key, created and revoked with operations on `mcp` and stored hashed, makes `/mcp` require it as a bearer token; without a key it is unauthenticated. There is no read-only setting: the policy refuses writes |
| People in the browser | The roles of the signed-in user, with the people system rules, kind `Person` |
| Visitors who are not signed in | The roles of the built-in user `anonymous`, default none, with the system rules of kind `Anonymous` |

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
| `SearchMetadata` | A query with the permissions of the policy: matches whose subject they show and whose metadata member they allow to read, matched on the metadata as they show it |
| `GetOrder`, `SetOrder` | `GetOrder`, a query, names only the children its permissions show, with read on the member that holds them, and so do the `order` in the metadata of a folder and its property `ChildOrder`, which holds the same order and which the tools do not list. A value filter of the configuration (`IMcpValueFilter`) reduces both where the built-in tools `get_property`, `browse`, `search` and `set_property` return or compare them, `McpMethodInvoker` hands the same filters to page expressions of a query through the permissions it gives the query (`IUserPermissions.GetValue`), the permissions of a person reduce both in their `GetValue` to the children the person is shown, with the same `ChildOrdering.FilterOrder`, and `SearchMetadata` matches the metadata as its permissions show it, so a hidden name cannot be confirmed by searching for it. Other results of `invoke_query`, such as the text of `ReadRawContent`, are method results and are not filtered. `SetOrder` needs `write` on the metadata member of the folder, as `SetMetadata` does. An order in `SetOrder` or in the `order` of `SetMetadata` that names a child the tools do not list is refused with the storage's text for a name that is no child, which names only the children they list, so unlisted and missing cannot be told apart |

**Page expressions.** `ReadEvaluatedContent` is a query of the shared page subject, and agents and people read the same page concurrently, so the reader is passed in: the method takes `IUserPermissions` as a `[FromServices]` parameter, and `MarkdownExpressionContext` resolves a property as missing, which renders empty like a secret, when the permissions do not allow to read it or a member its path reads through, or do not show its subject: `ResolveShownProperty` resolves the path through readable members only (`SubjectPathResolver.ResolveSubject` with the permissions, which checks the members an absolute path reads through from its cache entry) and then checks the property with `CanReadShown`. Value widgets bound by path, and the texts and value maps of widgets, use the same resolution. `McpMethodInvoker`, which every method and Markdown tool calls through, invokes each method with a service provider that resolves `IUserPermissions` to the permissions of the configuration's access policy, so agents and `/mcp` clients get the same rule in one place. In the browser the scope of the circuit resolves the permissions of the person.

- **The permissions** (`McpPageReadAccess`) build the property's path from the root and require `TryGetReadableProperty` to resolve that path back to the same property, so a property below a hidden branch renders empty although the policy allows to read it. The path names every member, the tools' segments and otherwise the member's name, so an expression may read a member the tools do not list, such as a derived title; a member that holds metadata only when the tools list it. A property without a path fails closed. A subject is shown when the tools build a path for it. A subject below a subject of a type excluded from agents is not shown, and its members are not readable. They never administer and hold no role. The answers are cached per member and subject for one query.
- **Values**: the permissions give the value of a readable property after the value filters of the configuration (`IUserPermissions.GetValue`), as `get_property` returns it, so a folder's `order` and `ChildOrder` name only the children the tools list.
- **Value maps**: the `map` pipe and the `Map` of a widget resolve the map with `ExpressionDisplay.ResolveReadableMap` and apply it only when its path reads through readable members, the permissions show the map and allow to read one of its members, so a map held by a hidden member, below a hidden branch, not shown, or whose members the policy all hides, reads as a missing one.
- **Queries only**: an operation gets `DenyAllPermissions`, as what it reads is not shown to the caller.
- Without a registered `IUserPermissions`, or without a service provider, a page read denies every property. The derived `Value` of a page expression is computed without a check, as it is shared by every viewer.

**Known limits.**

- A rule on a derived property applies to that property, even when it is computed from properties the policy does not allow to read.
- The raw source of a page (`ReadRawContent`, and `ReadEvaluatedContent` outside its expressions) includes the non-secret configuration values of its blocks, whatever the roles allow on their subjects.
- The history of a path that does not resolve is not available, whatever the access policy, so the history of a removed device is not available to agents and `/mcp` clients.
- A subject whose only members other than attributes are secrets is empty for every policy and hidden from every agent and `/mcp` client.
- A started OPC UA server (`HomeBlaze.OpcUa`) is an anonymous read-write gateway over what it exposes and ignores roles. Starting it needs only `execute` of **Start**, no `administer`; **Start** requires confirmation, so agents and `/mcp` clients cannot start it, while a person whose roles allow `execute` on it can.
- `RoleStore` does not check who changes a role: the operations that call it, through which the role dialog saves as well, require a full administrator. Code that calls it directly, such as a plugin, is trusted.
- That components run operations and queries only through `OperationInvoker` or `UserPermissionsExtensions` is checked by a test that scans the sources by method name (`WidgetOperationGuardTests`), not by the compiler, and plugins are not scanned.
- The other limits of roles, sign-in and agents are listed in [Known Limits](../../administration/authorization.md#known-limits).

## Authentication [Implemented]

People sign in with ASP.NET Core Identity on SQLite. The operator guide is [Signing In](../../administration/authorization.md#signing-in).

- **Database**: `authentication.db` in the data folder, or `Authentication:Database`, outside the browsable `Files` storage. It stays in `appsettings` because it must be known before anyone can sign in. `AuthenticationDbContext` uses Identity's tables; `ApplicationUser` adds `MustChangePassword`, `LastLogin`, `IsBuiltIn` and `AllowsEmptyPassword`, and `ApplicationRole` adds a JSON `Definition` and `IsBuiltIn`. Role assignments reference roles by id, so a rename keeps them. Setup codes are Identity user tokens, stored hashed with an expiry.
- **Graph as the API**: `Authorization.Roles` and `Authorization.Users` are runtime projections of the database. They change only through the operations and dialogs, which commit in a transaction and then refresh the projection, under one gate shared by the role and user stores. A refused or failed change leaves both unchanged.
- **Names**: user and role names are lowercase ASCII (`a-z`, `0-9`, `.`, `-`, `_`, at most 64 characters) and compare ordinally, so no name can look like another through case, Unicode normalization or invisible characters, and nothing depends on case folding. Only signing in lowercases the typed name; see [Names](../../administration/authorization.md#names). Display names, which nothing compares, refuse control, format (zero-width and bidirectional), separator and private use characters, so they cannot hide or reorder text in the user interface or the log.
- **Role saves never break a principal**: a save, rename or removal is validated against the role sets of every user, the built-in users and the agents, and is refused with the principals it would break, or when it would leave no unlocked full administrator, a user with `everything` and no deny rule on `administer` among their roles (`IsFullAdministrator`, `FullAdministrators`). Removing a role is refused while a user or an agent uses it. The user operations keep the same guard: removing, locking or setting the roles of a user is refused when it would leave no unlocked full administrator, and `--reset-admin` sets the roles of `admin` to `everything` alone.
- **Sessions**: the Identity cookie, `HttpOnly`, `SameAsRequest`, `Lax`, with a 14 day sliding lifetime for "keep me signed in". The authentication state is revalidated against the security stamp, so a lock, a password change, a reset or a removal ends open circuits at once when made through the store, and at the latest at the next check, which runs every 5 minutes. Every request with the cookie is also checked against the `UserStore` in memory (`SessionValidation`), the security stamp included, which the store keeps in step with every change it or the account endpoints make, so a removal, a lock, a reset or a password change refuses the older cookies at once on every endpoint, also after an unlock or a new password; a change of roles keeps the stamp and switches the circuit to the new roles at once. The check treats the request as not signed in and keeps the cookie, which it refuses on every request anyway, so that a refusal while the users load at startup, or of a parallel request right after a password change, before the browser stored the new cookie, ends no valid session. The hub validates the `Origin` header.
- **One gate in the app**: `AccountGate` in `Routes.razor` wraps the router. It renders the router only when `HomeBlazeAccessRequirement` passes (a signed-in person, or `anonymous` holding roles that compile) and the person need not change their password; otherwise the frame of the main layout (`LayoutFrame`) with the sign-in, setup or set-password dialog, and nothing while the authentication state is unknown. It decides again when the authentication state, the roles of the person or `anonymous`, or the policies change, and prerendering goes through it, so nothing of the app reaches a visitor it stops. The pages of the app and the hub therefore allow anonymous requests (`WithHomeBlazeAccountGate`); every other endpoint keeps the fallback policy, whose challenge redirects to the root with `ReturnUrl`, or answers 401 or 403 to a request that accepts JSON. A static page that is excluded from interactive routing and carries `[AllowAnonymous]` itself passes the gate, as it opens no circuit; today that is only the error page. It renders with the full layout and the deny-all permissions of the visitor, so a plugin page that opts in this way is trusted code.
- **Account endpoints**: `POST /account/sign-in`, `/account/password`, `/account/setup`, `/account/display-name` and `/account/logout`, with antiforgery tokens, the sign-in rate limit except for sign-out, and JSON answers (`redirect`, `error` or `errors`) that never echo a password. A post to `/account/password` or `/account/display-name` without a session, such as one whose cookie the session check refused, gets 401 with the root as `redirect`, so that the dialog loads the app, which asks to sign in. A circuit cannot set cookies, so the dialogs post plain HTML forms, whose inputs are not bound to the circuit, with `fetch` from a small script, and load the redirect anew; passwords never travel through the circuit. The endpoints share their logic (`PasswordSignIn`, `PasswordChange`, `SetupCodeSignIn`): one refusal message with one password hash verified per refusal, lockout, a security stamp check before a password change, the refusal of an administrator lock and failed accesses counted for a wrong current password.
- **Files of the storages**: `GET /files/{path}` (`MapStorageFiles`) keeps the fallback policy and serves a file only when `IUserPermissions.CanShow` shows it, the decision the pages use, else 404 as for a missing file. The endpoint does not use the authentication state of a circuit: `IRequestUserPermissionsFactory` decides once, from the user of the request and the roles the `UserStore` holds now, by the rules of `PolicyUserPermissions`, for that request only. It is not revalidated, as `SessionValidation` has checked the cookie of the request already.
- **Must change the password**: besides the gate, a person with the claim gets deny-all permissions and a user administration that refuses every change in the circuit, and every endpoint other than the app, the hub, framework files, static assets and the password and sign-out endpoints answers 403.
- **Rate limits**: per client address (IPv6 per /64) on posts to the sign-in, password and setup endpoints (`LoginRateLimit`), and on requests that are not signed in to the pages of the app and the hub (`UiStartRateLimit`), which bounds the circuits such visitors can open; signed-in requests to the app are not limited. The numbers are in [Signing In](../../administration/authorization.md#signing-in).
- **First start and recovery**: a seeded `admin` with `everything`, an empty password and a marker file next to the database, so that a lost or emptied database never reopens the empty password while the marker survives next to it, see [First Start](../../administration/authorization.md#first-start). `--reset-admin` resets and exits, see [Recovery](../../administration/authorization.md#recovery).
- **Reverse proxy**: forwarded headers apply only for the proxies listed in `Authentication:ForwardedHeadersKnownNetworks` and `Authentication:ForwardedHeadersKnownProxies`; `Authentication:PathBase` sets an optional base path.
- **Agents and chat**: the chat, timeline, activity and memory of an agent require every role of the agent or `everything`, because they are shared by everyone who chats.
- **Enforcement in the UI**: `IUserPermissions` (in `HomeBlaze.Authorization.Abstractions`, so plugin widgets use it too) answers `CanRead`, `CanWrite`, `CanInvoke`, `CanAdminister` and `CanReadHistory` for a member, `CanShow` for a subject and `HoldsAllRoles`, for the person of the circuit. The default registration denies everything. Components that need it derive from `PermissionsComponentBase`. `PropertyWriter`, `OperationInvoker` and the value readers of widgets take the permissions, and paths configured on widgets resolve under the viewer's policy, so a path the viewer may not read looks like a missing one. Read denies apply to member paths, not to where data flows.

### Developer Notes

- Entity Framework migrations of the authentication database live in `HomeBlaze.Authentication/Data/Migrations`. Add one with `dotnet ef migrations add <Name>` for that project, and keep the `// <auto-generated />` header on the first line of each generated file, including the designer file and the model snapshot: the analyzers skip generated files, and without the header the Sonar rule S138 fails the build on them.
- Downgrading to a version from before sign-in removes sign-in, and a downgrade between versions with sign-in after a database migration is not supported and may fail. See [Upgrading to sign-in](../../administration/installation.md#upgrading-to-sign-in).

## Inter-Node Authentication [Planned]

Planned. WebSocket connections between instances (satellite-to-central, primary-to-standby) need authentication to prevent unauthorized nodes from joining the topology.

Likely approach: mutual TLS or token-based authentication on WebSocket connections.

## Per-Client MCP Access [Planned]

Planned. Today every `/mcp` client shares the roles of the built-in user `mcp`, and an optional shared key authenticates them. With a client identity, such as per-user tokens for MCP clients and scripts, the MCP session would carry the roles of that user and get its own policy from `AuthorizationService`, as an agent does.

## Key Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Authorization model | Roles as data, rules selecting by path, type, member and tag, compiled per principal | Operators define roles without code; subject types need no attributes, as tags come from existing markers |
| Built-in roles | `reader`, `operator`, `everything` in code | Always available, so an installation without custom roles works, and `operator` leaves the choice of tools to `AllowedTools` |
| System rules | Fixed deny rules per kind of principal | Secrets, configuration, confirmation, agents, administration and the roles themselves must never depend on a role being written correctly |
| Two gates for agents | `AllowedTools` decides the tools, roles what they reach | Tools stay a coarse, visible switch; roles add the fine-grained scope |
| Several `Authorization` subjects | None is used | Merging two role sets silently could grant more than either intends |
| Page expressions | The reader's `IUserPermissions` as a service parameter of the method | The shared page subject receives the reader with each call, and a missing reader fails closed |
| Configuration | Administration, not writing; full administrators only | Changing configuration can run code or point the application at its own data; scoped administration needs a per-type classification |
| Agents | Never administer; a person chats only if they hold every role of the agent and the agent holds each of their roles with deny rules | Agents are steered by text they read; one shared conversation and memory must not leak or escalate between people |
| Deny rules when acting through another principal | The roles assigned, the user acted on and the agent chatted with hold each of the person's roles with deny rules, full administrators included; locking, removing, taking all roles away, and taking roles away while the user keeps the person's roles with deny rules that they hold are exempt | Deny rules apply across all roles of one principal, so a principal without the role is not bound by them; taking away a role with deny rules lifts its deny rules and grants access |
| Inter-node auth | Deferred | Focus on graph authorization first |

## Open Questions

- A "preview as a user" tester, two-factor sign-in or passkeys, a kiosk device sign-in with a long session, and sign-in through an external identity provider
- Scoped administration of configuration and pages, which needs a per-type safety classification
- Inter-node authentication mechanism (TLS, tokens, certificates)
- Per-user tokens for MCP clients and scripts, and the MCP session identity model
- Authorization for cross-instance operation proxying
- Audit integration: logging authorization decisions (see [Audit](audit.md))
