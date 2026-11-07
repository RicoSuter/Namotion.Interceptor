# Authorization: Design

This document describes how `Namotion.Interceptor.Authorization` decides permissions and why it is built the way it is. For its use, the selector and pattern tables, the compile errors and the JSON form, see [Authorization](../authorization.md). Examples use the registry's default path provider, whose separator is `.`.

## Problem

A subject graph exposed to principals other than its owner (AI agents, remote clients, operators with limited duties, protocol connections) needs finer permissions than "read-only" or "everything". The questions are per member and per branch: may this principal read the temperature of every room but not the Wi-Fi password, operate the lights but not in the bedroom, run queries but not operations that need a confirmation. A flag on the server or a filter per tool answers none of these, and attributes in device code would bake one application's role ladder into libraries that serve many.

## Goals

1. **Default deny.** Nothing is allowed unless a rule allows it.
2. **Roles are data.** An application stores and edits roles like any other configuration; nothing about a role is compiled into code.
3. **Application-agnostic.** The library knows the graph through the registry and nothing else. Tags, actions beyond `read` and `write`, and where roles are stored belong to the application.
4. **Cheap enough per member.** Browsing a tree asks one question per member of every visible subject, so the common question allocates nothing and takes nanoseconds.
5. **Explainable.** A permission tester can show which role and rule allowed or denied a question, and why a subject is hidden.
6. **Fail closed.** Whatever the library cannot decide, a role it cannot compile or a path it cannot list, never widens what a principal may do.

## Model

| Concept | What it is |
|---|---|
| Member | A `RegisteredSubjectProperty`: a property, a method an application registers as a property, or an attribute. An attribute is evaluated on its own, with its own tags, and is named `Property@Attribute` for member globs. |
| Rule | Four selectors (`Paths`, `Types`, `Members`, `Tags`) and a list of actions. It applies when the action is listed and every non-empty selector matches. |
| Role | A name, the names of included roles, allow rules and deny rules. Includes are transitive and may form a diamond. |
| System deny rules | Deny rules of the application that apply to every principal, reported as the reserved role `System`. No role can override them. |
| Principal | Whoever holds a set of role names. The library has no principal type; the role names are the principal. |
| Definition | `AuthorizationDefinition`, the roles, system deny rules, known actions, tag provider and path provider of an application, validated and compiled once. Immutable and shared across threads. |
| Policy | `AuthorizationPolicy`, the compiled rules of one principal's roles and their includes, plus the system deny rules, built from a definition. Immutable and shared across threads. |

The library asks the tag provider for an attribute's own tags rather than inheriting its property's, so that a deny on writing configuration need not block editing metadata an application stores as an attribute of a configuration property. A provider that wants inheritance returns the property's tags.

### Actions are names compiled to bits

The library defines `read` and `write`. Everything else (executing a method, reading history, a connector's own access kinds) is an action name the application defines and passes as the known actions of the definition. An enum would force every application onto the library's list or force a library change for each new kind of access; a name lets an application add `execute` or `history` without the library knowing what either means.

Names are resolved once, at compilation: `read` is bit 0, `write` bit 1, and every further known action the next bit of a `ulong`. A compiled rule keeps its actions as a mask, so the first check of every rule is one bit test. A `ulong` caps the set at 64 actions, `read` and `write` included, which is far beyond what an application defines and keeps the test a single instruction. A rule naming an action outside the known set fails compilation, so a typo cannot silently grant or deny nothing. Asking a policy about an unknown action throws, since that is a programming error of the caller, except on `DenyAll`, which answers no to everything.

### Tags come from the application

A rule selects members by meaning (state, configuration, secret, an operation that needs confirmation) through tags. The library does not define attributes for these. Code already carries semantic markers of its own, and permission attributes would duplicate them and put role decisions into libraries that cannot know the installation. `IMemberTagProvider` maps whatever an application has (attributes, registry attributes, naming conventions) to tag names, and the mapping can change without touching rules or device code.

The provider is called on every evaluation of a rule with tags, possibly concurrently, so it returns cached arrays. It returns an `ImmutableArray<string>` rather than an interface, so comparing tags never allocates an enumerator, whatever collection a provider would otherwise have chosen.

## Evaluation

### Allow union minus deny

The mental model is a set over the whole graph: start empty, add everything any allow rule of the principal's roles matches, remove everything any deny rule of those roles or of the system matches. What remains is allowed. `IsAllowed` computes `any allow matches && no deny matches` for the single question asked, which gives the same answer without materializing the set. Order of roles and rules does not change the answer.

A deny removes whatever it matches, wherever in the include chain it sits. A role that includes another role cannot allow back what the included role denies. This is deliberate: a precedence by include depth or by rule specificity would make a role's effect depend on who includes it, and a reader of one role could no longer tell what it guarantees. The consequence for role design is that reusable roles should mostly allow, and carve-outs belong in the role that composes them. Several roles on one principal behave exactly like one role including them all.

System deny rules use the same mechanism with one difference: they are not data the application's users edit. An application puts its invariants there, such as never revealing secrets to an agent or never letting a principal read the roles that govern it, and nothing a role says can switch them off.

### Selector order and the match context

`Matches` checks a rule's selectors cheapest first: the action bit, the member glob, the tags, the type, the path. A rule fails at the first selector that does not match, so a path is enumerated only when a rule with paths survives every other selector. Denies are evaluated only after an allow matched, which keeps a denied-by-default question cheap.

One question can test many rules, and several of them may need the same derived facts about the member. A `MatchContext` struct, passed by reference through the allow and the deny pass, computes each fact at most once per call and only when a rule needs it: the member name (which allocates for an attribute, see [Performance](#performance)), the tags from the provider, and the subject's paths. Type names are cached process-wide per runtime type, since they never change.

### Explain

`Explain(member, action)` runs the same passes and returns the first matching allow rule and the first matching deny rule with their roles. Rules are kept in closure order, included roles first, each role's rules in their written order, and the system deny rules last, so the report names the first deciding rule in that order and names `System` only when no role's deny matched. It is meant for a permission tester and for tests, not for hot paths. `ExplainVisibility(subject)` is described under [Subject visibility](#subject-visibility).

## Paths

### Patterns match member paths exactly

A path pattern matches the paths of members, and only whole ones, the way a file glob matches file paths. A member's path is the path of its subject followed by the member's segment, so `Rooms[Kitchen].Sensor.Temperature` is one member and `Rooms[Kitchen].Sensor` is another, the property of the kitchen that holds the sensor. `*` alone is one segment, `**` is zero or more segments anywhere, and `*` inside a segment is a glob over its text. So `Devices.Sonos.Play` names one member, `Devices.Sonos.*` every member of that subject, and `Devices.Sonos.**` every member of it and of every subject below.

Exact matching makes a rule cover what it spells and nothing more, which is the least surprising reading for anyone who knows file globs, and the reach of a rule is visible in its text. The cost falls on rules for a branch, which end in `.**`: a deny on `Devices.Sonos` alone denies only the property holding the subject, and a deny on `Devices.Sonos.**` is the branch. Matching by prefix against subject paths is the alternative, see [Alternatives considered](#alternatives-considered).

The `Members` selector stays: it matches a member's name wherever the member is, which a path pattern spells as `**.Name`, and combines with types and tags without naming a location.

A member's segment is the provider's segment for the property, or its `BrowseName` when the provider excludes it. The fallback matters for denies: a member the provider leaves out of paths still has a name a rule can match. It widens an allow the same way: any allow pattern that matches an excluded member's `BrowseName`, such as `*`, `**`, a segment glob or another member's segment of the same text, grants that member, which the [user documentation](../authorization.md#path-patterns) warns about. An attribute's segment, and the link through an attribute that holds subjects, come from the same place, so attribute paths follow the registry's path syntax rather than one of the library's own. The registry's writer and reader spell an attribute by its name alone unless the provider maps it (`Devices.Sonos.Unit`, and `Devices.Sonos.Probe.IsOn` below a subject-holding attribute), so attributes of the same name on different properties of one subject share a path. Since the segment comes from the provider, a change of the registry's attribute syntax reaches the member segment and `SubjectPaths` without a change in this library.

Which links a path may pass follows the registry as well, which builds and resolves paths only through properties the provider includes. A link counts only through a property the provider effectively includes (`PathProviderExtensions.IsPropertyEffectivelyIncluded`): the provider includes it and, for an attribute, the property it belongs to, recursively. A subject held by the attribute `Probe` of an excluded `Temperature` therefore has no path, here as in the registry, and its paths count as incomplete. The member segment does not require inclusion: it names the member, not a link, and the `BrowseName` of an excluded member lets a deny match a member the provider leaves out. Likewise, the registry builds no path to an `[InlinePaths]` property whose segment another included property has, but this library still names that member by its segment, since a member segment does not require a path, so a rule naming that path matches both members. Subject paths are unaffected for keys the reader takes bare, because a link through the dictionary always carries a key; a key that needs the explicit form has no path while the property's own segment is shadowed, as in the registry.

Patterns are parsed with the parser of the `PathProviderBase` given to `AuthorizationDefinition.Create`, the provider an application's tools address the graph with, so a segment means in a rule what it means in a request. A pattern segment without an index matches a path segment with or without one, so `Line.Machines.**` covers every machine. A member has no index, so a pattern whose last segment has an index, or the separator alone, could match no member. Both are rejected with an error naming the fix (`Rooms[Kitchen].**` or `Rooms[Kitchen].*`): a pattern naming a subject reads like a rule for that subject, and as a deny that silently matches nothing it would be the most dangerous misreading of exact matching. The registry's parser skips empty segments, so a doubled or trailing separator is rejected too, as it would otherwise compile to a pattern other than the one written. Such an error in a role's rule is reported by `Compile` to the principals that reach the role, and in a system deny rule by `Create`, see [Compilation and failure](#compilation-and-failure). The full table is in [Path patterns](../authorization.md#path-patterns).

A pattern names paths from the root, and an attached registered subject without parents is a root whose path is empty (see below), so the members of a root have one-segment paths. `**` matches every member of a subject that has a path, `*` every member of a root. A leading separator is optional and means nothing: the registry's `TryGetPath` builds paths without one, and an application's tools may build them with one, as absolute file paths do, so both spellings of a path compile to the same pattern. A pattern written for another provider, such as `/Home/Kitchen/**` under the default provider, would read as one segment and match nothing, so a pattern whose first character is `/` or `\` that is not the provider's separator fails with a hint naming the separator. Only the first character is checked, since a bare `[InlinePaths]` key may contain either character; such a key that starts with one of them cannot open a pattern in its bare spelling and is written in the explicit form, `Areas[/Garage].**`; `Home/Kitchen` without it still compiles as one segment and matches nothing, one more pattern only a check against the graph finds.

A subject without parents is not always a root. A detach unlinks the detached subject from its parent and the subjects it holds from it, under the registry's lock, and unregisters each of them when the detach reaches it. A caller can also hold a member it resolved before the detach, or have read a parent list just before it changed. Treating any parentless subject as a root would give it the empty path, and every path deny would stop applying to the members below it. A root is therefore a subject without parents whose `RegisteredSubject.IsAttached` is true. The registry clears the flag when it removes a subject's last parent, in the same change and before the removal is visible, and when it unregisters the subject; outside the subject's own detach, the registry removes a last parent only in the change of a parent that detaches, and the lifecycle then detaches the subject too, so the flag never clears on a subject that stays in the graph. A move that removes the old reference first detaches the subject and attaches it again as a new instance, while a move that adds the new reference first keeps the instance attached with a parent throughout, as a subject held by two properties keeps the other parent, also when the parent it inherited its context from releases it, and reassigning a value, reordering a collection or re-keying a dictionary entry keeps the parent. Every reader reads the flag after the parents, so a reader that sees the parents empty after a removal sees the flag cleared. The flag is checked for every subject a path passes, in `GetPaths`, in `SubjectPaths` and in `IsReachable`, and a subject that is no longer attached makes the paths incomplete or the walk fail. It is a field read, so the check costs nothing measurable.

A question asked while a detach runs therefore sees each subject of the detached branch either still linked on its old path, where a deny for that path applies, or no longer attached, where every path deny applies. An allow rule with paths can still match through the old path for as long as the old link is visible, which is the usual quiescent consistency of evaluation.

### Every path of a subject

A subject can have several parents, for example a light held both by its room and by a group of favorites. A rule for `Rooms[Bedroom].**` must apply to that light's members however a request reached it, or a deny could be bypassed through the other parent. The policy enumerates every path of a subject, one per chain of parents up to a root, and both kinds of rules match through any of them:

- An allow with paths matches if any path matches: the light is in an allowed branch.
- A deny with paths matches if any path matches: the light is in a denied branch, and the deny wins wherever the light is also reached.

`SubjectPaths` follows a chain of single parents without a search, which is the common case. At the first subject with several parents it switches to an iterative depth-first search over all chains, pruning a chain that returns to a subject already on it, so cycles end. Owners are not memoized, because every chain is wanted, so the search is bounded instead (see [Caps and failing closed](#caps-and-failing-closed)).

### Links carry every spelling

A path is a list of `PathLink`s from the root down to the property holding the subject. A link holds the property's segment, the index text and, for a key of an `[InlinePaths]` dictionary, the bare key when the registry's path reader resolves it without the property's segment. So `Security.Roles[Reader]` and `Security.Reader` name the same subject, and a pattern written in either spelling matches its members. A link also holds its structural spellings, which deny rules match: the property's segment and, for an `[InlinePaths]` key, the bare key whenever it is one non-empty name without the separator or `[` that differs from the property's own segment, both whether or not they resolve right now. A property added at runtime, such as a dynamic property or attribute named like a key or like the `[InlinePaths]` property, can shadow a spelling; an allow in that spelling then stops matching, as it no longer reads back, but a deny keeps applying. Over-approximating a deny this way is fail closed: a deny written as the bare key also covers a key that a property of the same name already shadows. A pattern segment with an index never matches the bare spelling, since the bare spelling is a name only.

The rules for which spellings a key has follow the registry's reader: the bare key is offered when it is one non-empty name without the separator or `[`, differs from the inline property's own segment and resolves to the inline property, so it is not shadowed by another property of the same name. The explicit form is offered while the property's own segment still resolves to it. A key that resolves in neither spelling makes the path unwritable, which counts as an incomplete path set. The same holds for an `[InlinePaths]` property used without an index, which holds a single subject: its segment is offered while it resolves to the property, as the registry's `TryGetPath` writes it only then, and otherwise the path is unwritable.

These rules mirror the registry inside this library, built only on the registry's public API (`PathProviderBase`, `PathExtensions.FormatPathIndex`, `InlinePathsAttribute`). That keeps the released registry package unchanged by authorization. The cost is that the mirror can drift when the registry's path rules change. The cross-check tests in `SubjectPathsTests` guard it: for plain properties, dictionary entries, collection positions, bare inline keys, inline keys shadowed by a property or an attribute, keys that need quoting or equal the property's own segment, mixed paths and subject-holding attributes, every combination of the spellings a link offers must resolve to the subject through the registry's `TryGetSubjectFromPath`, the spellings it leaves out must not, the structural spellings must include every spelling that resolves, and the canonical spelling of the first path must equal what the registry's `TryGetPath` writes. For a subject held by an attribute of an excluded property, or by an `[InlinePaths]` property without an index whose own segment another property has, the library must list no path while the registry's `TryGetPath` builds none and `TryGetSubjectFromPath` resolves none.

### Matching

`PathPattern.Matches` takes the links of a subject's path and the member as one more link, so no member path string is built. It is the classic glob over segments: it walks the pattern and the member path together, a `**` first matches zero segments, and on a mismatch it backtracks only to the last `**` it passed, which then takes one more segment. That suffices because `**` matches any number of segments and every other pattern segment exactly one: once a later `**` has matched, letting an earlier one consume more segments can only leave less path for the rest of the pattern. The match succeeds when the whole member path is consumed and only `**` segments remain in the pattern. The matcher has no recursion and no allocation, and its worst case is the pattern length times the path length. Segment globs use the same single-backtrack technique over characters (`Glob`).

The member's segment is looked up once per question and only when a rule with paths gets that far; it is a string the provider already holds.

### Caps and failing closed

A dense graph can have a number of paths exponential in its depth, so enumeration is capped: at most 64 paths, and a search budget of 64 parent links per allowed path, 4096 in all, which bounds the work in a densely connected component even when it yields no path at all. A cap reached is one of the cases in which the set of paths is incomplete; [Subjects with several paths](../authorization.md#subjects-with-several-paths) lists them all.

On an incomplete set an allow with paths matches only through the paths that were listed, and every deny with paths matches. Denies with paths fail closed: the missing path may be exactly the one the deny names, and the library cannot tell. The alternative, matching denies only against the listed paths, would let a misconfigured provider or an unusually dense graph widen a principal's permissions without any sign. The cost is that such a subject matches path denies that have nothing to do with it. The other selectors of a deny still apply, which is one more reason to give a path deny a second selector.

### Known limits

The limits a rule author must know are listed under [Known limits](../authorization.md#known-limits) of the user documentation; this section gives the design reasons.

- **A subject held twice in one collection or dictionary.** The registry records one parent per property, so the second occurrence is not enumerated. The fix belongs in the registry, which should keep one parent per occurrence; scanning the container for every occurrence here would mirror more of the registry and cost a scan per indexed link.
- **Patterns that match nothing.** Compilation sees no graph, so it rejects only patterns that can match no member in any graph; a misspelled name compiles, and so do the other cases listed under [Path patterns](../authorization.md#path-patterns). A check of patterns against a live graph, such as a role editor would run, is the place to report them.

## Subject visibility

`CanRead(member)` answers for one member. Browsing needs a different question: whether the subject itself is visible (`IsVisible(subject)`). A subject is visible when at least one of its members is readable and some chain of parents leads from it to a root on which every subject-holding property is readable. The holding properties are members of the parents and are matched by their own member paths. A deny with `read` on the property holding a subject, `Devices.Cameras`, therefore still hides the whole branch beneath it from browsing, as an unreadable link makes a subject unreachable even though its own members are readable. Those members stay readable when asked for by path; a deny on `Devices.Cameras.**` makes them unreadable as well.

The link of a chain is a member of the parent subject, not of the child. A rule that allows reading `Rooms[Kitchen].**` makes the kitchen's members readable but not `Home.Rooms`, which belongs to the root; the kitchen stays hidden until some rule makes the links on its way readable. An application usually tags subject-holding properties so one rule grants them.

Visibility uses reachability through any parent rather than the first parent, for the same reason paths use every chain. The registry's first parent depends on attach order, which must not decide visibility, and a subject a principal can reach by browsing through readable links must not be hidden from it. The two notions do not conflict: a `read` deny on the path `Rooms[Bedroom].**` makes every member of a light there unreadable, so the light is hidden whatever other parent it has, while a deny on one link only cuts that route.

`IsReachable` walks parents depth first and stops at the first route to a root. The walk is iterative, so no depth of graph can overflow the stack. Along a chain of single parents it allocates nothing. At a subject with several parents, or deeper than 32 levels, it adds the subject to a visited set shared by the whole walk. A subject already in the set is not explored again: its readable links and parents do not depend on how the walk arrived, so a second visit cannot find a route the first missed. Every subject with several parents, and every subject past depth 32, is therefore explored at most once, which bounds the walk and ends cycles of any shape, including two rooms that hold each other twice or three rooms that point at each other.

`ExplainVisibility(subject)` returns a `VisibilityDecision`: whether the subject is visible, whether it has a readable member at all, and otherwise the first unreadable subject-holding property the walk met, for a tester to say why a path is hidden. It reports no property when the subject is visible, when no member is readable (the cause is then on the subject itself), and when the walk met no unreadable link, as for subjects that only hold each other in a cycle with no root. While the graph keeps changing it fails closed with neither, see [Threading](#threading).

## Compilation and failure

`AuthorizationDefinition.Create` checks the names of all roles and the known actions, assigns the action bits, and parses every selector of every rule once, the system deny rules included. `Compile` then resolves the principal's role names and their includes and concatenates the compiled rules of that closure, so a principal's policy needs no parsing and an application validates its rules once rather than once per principal. The checks and on which side each is reported are listed under [Usage](../authorization.md#usage).

| Reported by | Why there |
|---|---|
| `Create`: the names of all roles | Names must resolve unambiguously before any closure can be computed, and a role named `System` would be indistinguishable from the system rules in `Explain`. |
| `Create`: the known actions and the system deny rules | Every principal depends on them. |
| `Compile`: the rules of the closure | A role's rules are parsed once by `Create`, which keeps the error; only principals whose closure contains the role get it, so a broken role does not lock out principals that do not use it. |
| `Compile`: the principal's names and the includes of the closure | Only these roles take part. The includes are checked by the walk that resolves the closure, so an unknown include, a cycle or a chain nested too deep fails exactly the principals whose walk reaches it. |

A principal whose definition cannot be created or whose policy cannot be compiled gets `AuthorizationPolicy.DenyAll`, never a policy built from the parts that compiled. Dropping a broken rule could drop a deny, and dropping a broken role could drop its denies, either of which widens permissions. `DenyAll` knows no actions and answers no to any action, including application actions, so a caller does not have to special-case it.

The include closure is resolved recursively; the depth cap of 64 turns a pathological include chain into a compile error with the role's name instead of a stack overflow. Roles reached through several includes, as in a diamond, are resolved once. `Roles` on the policy lists the closure in resolution order, included roles first, which is also the order of the rules and of `Explain`.

`PermissionRule` and `AuthorizationRole` are records with value equality over their arrays: element by element and in order, role names, includes, actions and tags ignoring case like resolution and matching, the other selector values exactly. An application can therefore tell whether a role really changed before recompiling, and tests can compare rules. Order counts because it decides which rule `Explain` reports.

## Performance

A policy is built for many cheap questions. The common question, a member decided by an action, member, tag or type selector, allocates nothing: rules are arrays of a sealed class, the action is a bit test, the context is a struct on the stack, tags are compared over the provider's array, and type names come from a process-wide cache.

The allocations that remain each have a reason:

- **Path rules.** When a rule with paths survives its other selectors, the subject's paths are enumerated into lists the thread reuses, so a chain of single parents allocates nothing once they have grown. A search over several parents allocates its stack and the set of subjects on the current chain, and an instance whose lists have grown past their caps is dropped after the call instead of being kept by the thread. The paths are memoized for the rest of the call, and `IsVisible(subject)` carries them from member to member. When every ancestor of a subject up to a root has a single parent, the subject has one path, found without a search, and the walk takes each owner's path from it by dropping the last link, so such a chain of any depth is enumerated once. Elsewhere each owner's paths are enumerated for its link, because a derived list could differ from the owner's own in whether the search budget lasted, so a long chain below a subject with several parents still costs one enumeration per link.
- **Attribute names.** A member glob matched against an attribute builds its `Property@Attribute` name.
- **Types.** The first type rule evaluated for a runtime type caches that type's simple and full names, its base types and its interfaces.
- **Visibility.** `IsVisible(subject)` allocates the visited set when a subject on the way has several parents or the chain is deeper than 32 levels, and the list of parents still to try at a subject with several parents.

A deny rule with only `Paths` is reached by every member that an allow matched for that action, and proving it does not apply requires enumerating the paths. The [user documentation](../authorization.md#performance-and-threading) therefore advises giving a path deny a second selector (a type, a member glob or a tag) and putting path rules only into the roles that need them, not into a reusable base role every principal pays for.

`AuthorizationBenchmark` evaluates a policy of 24 rules, among them ten allow rules with paths and ten deny rules with only paths, against a graph of 50 floors with 40 lamps each (2050 subjects); its rows `UnderReadPathDeny` add a 25th rule, a `read` deny on one floor's branch with only paths. The allocations in steady state, which unlike the times do not vary between runs and machines:

| Question | Benchmark | Allocated |
|---|---|---|
| A read decided by a tag rule | `ReadByTag` | nothing |
| A write where the deny rules have only path selectors and none matches | `WriteNotUnderDeniedPath` | nothing |
| A write denied by a path rule | `WriteDeniedByPath` | nothing |
| `IsVisible(subject)`, no path rule applies to `read` | `CanReadSubject` | nothing |
| `IsVisible(subject)` with a `read` deny with paths, so its members and every link to the root need paths | `CanReadSubjectUnderReadPathDeny` | nothing |
| A browse-shaped pass: `IsVisible` of every subject and `CanRead` of the members of the visible ones | `BrowseShapedEvaluation` | nothing on the calling thread, see below |
| The same pass with a `read` deny with paths | `BrowseShapedEvaluationUnderReadPathDeny` | nothing on the calling thread, see below |

The writes allocate nothing because the lamp's single path is built into the lists the thread reuses. For the browse-shaped passes BenchmarkDotNet reports a few dozen bytes per pass. Its counter is process-wide, while a count of the calling thread's own allocations (`GC.GetAllocatedBytesForCurrentThread`) over 1,000 passes after warm-up is zero for both, as it is for every other row over 100,000 calls, so those bytes come from other threads of the process.

### Threading

A definition and a compiled policy hold no mutable state and are shared freely. Evaluation reads the registry's live parents and caches nothing between calls. A question about a member (`IsAllowed`, `CanRead`, `CanWrite` and `Explain` of a member) answers as of one instant of the graph, also while subjects move: it reads the registry's structure version before it enumerates the paths and again afterwards, as [Structure version](../registry.md#structure-version) describes, and keeps the paths only when both reads return the same even value, so the enumeration overlapped no registry change, not even part of one. An odd or changed value discards the attempt, and after forty attempts, with a wait between them that spins at first and then yields the thread so that a burst of changes usually ends meanwhile, the question fails closed: the paths are incomplete and none is listed, so a deny with paths applies and an allow with paths does not. The answer for a root or a detached subject rests on one subject's parents and attachment, which hold together at the first read, so it needs no version. The policy takes the registry from the context of the subject it evaluates, so the version always belongs to the registry that holds the subject; the paths of a subject whose context no longer reaches a registry, which is detached, are incomplete the same way. The version covers parents and attachment, not the subjects' properties, which decide whether an `[InlinePaths]` key is listed bare, so an allow written with a bare inline key may still match while a property that shadows the key is being added concurrently. A deny is not affected, since it matches the structural spellings whether or not a property shadows them. A question about a subject (`IsVisible` and `ExplainVisibility`) answers as of one instant the same way: the checks of its members, the walk to a root and the checks of the links on the way run in one attempt between the two reads of the version, so the members are judged by the paths and the subject reached through the parents of the same instant. After forty attempts that each overlapped a change it fails closed: the subject is hidden, with no readable member and no denied property. A subject whose context no longer reaches a registry is detached and never visible; its evaluation runs once, unvalidated, only to report why it is hidden. The version counts every registry change, not only those on the evaluated paths, so a registry that changes without pause anywhere in the graph can make a question fail closed: with a writer that never pauses, about one question in a hundred thousand fails closed when it moves single subjects, and about one in a thousand when it attaches and detaches a subtree of 500 subjects. Every question asked once the changes pause answers for the settled graph. A caller that wants one consistent answer for a whole pass, such as a tree browser memoizing `IsVisible(subject)`, keeps that memo per call.

## Integration points

An application supplies:

- **Roles**, stored and edited wherever it keeps configuration, and the role names each principal holds. It creates one definition from the roles, compiles one policy per distinct set of role names, caches it, and swaps both atomically when a role or the set changes, since neither changes after it is built.
- **Known actions**: `read`, `write` and its own, such as `execute` for methods or `history` for recorded values.
- **A tag provider** that maps its semantic markers to tags, including a tag for subject-holding properties so that a role can grant the links visibility depends on.
- **The path provider its tools use**, so patterns mean what request paths mean.
- **System deny rules** for invariants no role may break, and `DenyAll` for any principal whose definition cannot be created or whose policy cannot be compiled.

It enforces at every entry point where a principal touches the graph: every read, write and method or operation call checks the member and the action; browsing and search check `IsVisible(subject)` and leave out what is hidden, typically answering a request for a hidden member as not found so that a denial does not reveal what exists; expressions or templates evaluated on behalf of a principal resolve values through that principal's policy. An operation whose argument names another subject asks two ordinary questions, one for the operation's member and one for the target's member.

The MCP library's tools consult an `IMcpAccessPolicy` per member and action name (see [Access Policy](../mcp.md#access-policy)), so an application connects a policy to them through a small adapter, without the MCP library referencing this one.

Planned follow-ups:

- The application integration: role storage, the tag provider, system rules, enforcement in every tool, and operator documentation.
- A role editor with a permission tester built on `Explain`.

One question is deferred to the integration. Patterns match paths from the root, which is any attached registered subject without parents. A server that exposes a subtree as its root resolves request paths relative to that subtree, while rules still match from the graph's root, so such a server may need patterns that match relative to its root.

## Alternatives considered

**Patterns matched by prefix against subject paths.** A pattern could match the paths of subjects, with a pattern on a subject covering its subtree. Rules for branches would be shorter, but no pattern could name one member by its location or a subject's members without its subtree, and a pattern that reads like one subject would reach a whole branch. Member paths matched exactly, like file globs, make every rule cover what it spells; see [Patterns match member paths exactly](#patterns-match-member-paths-exactly).

**Path strings instead of links.** Paths could be enumerated as the strings the registry builds and parsed back into segments for matching. That allocates a string and a segment list per path, and a string carries one spelling: matching both spellings of every `[InlinePaths]` key would need a string per combination of spellings, doubling with every such key on the path. Links carry all spellings per segment, so one path covers every combination and no string is built.

**Enumeration in the registry.** Enumerating every path is a registry concern and could be registry API. It lives in this library so that the released registry stays unchanged while the library incubates, and so that the spelling rules authorization needs do not become registry API for every consumer. The price is the mirrored link rules, which the cross-check tests guard.

**Re-reading each subject's parents instead of the structure version.** An evaluation could record the parents array and attachment of every subject it reads and accept when a second read returns the same ones, which a change elsewhere in the graph does not disturb. It cannot see a change in progress: the registry updates the indices of a reordered collection's items one after the other, so a read that takes one item's index before the update and another's after it, and re-reads both before the update reaches the first, accepts an order that never existed and can miss a deny on an index. The structure version is odd for the whole change, and two reads of it cost less than recording and re-reading every subject.

**Fail open for incomplete path sets.** Matching denies only against the listed paths would keep an excluded property or a dense graph from denying unrelated members. It would also let exactly those cases bypass a deny, and neither is visible to the person writing rules. Failing closed makes the failure show up as a denial a tester can explain.

**A path memo per subject across calls.** Caching each subject's paths would save the enumeration on repeated questions, but needs invalidation on every structural change of any ancestor, and a stale entry could miss the deny of a branch the subject has just moved into. The memo is per call instead: per member for `IsAllowed`, and per subject across its members inside `IsVisible(subject)`, where all members share the subject's paths.
