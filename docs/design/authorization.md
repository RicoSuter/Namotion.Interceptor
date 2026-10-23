# Authorization: Design

This document describes how `Namotion.Interceptor.Authorization` decides permissions and why it is built the way it is. For its use, the selector and pattern tables, the compile errors and the JSON form, see the [library README](../../src/HomeBlaze/Namotion.Interceptor.Authorization/README.md).

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
| System deny rules | Deny rules an application passes with every compilation, reported as the reserved role `System`. No role can override them. |
| Principal | Whoever holds a set of role names. The library has no principal type; the role names are the principal. |
| Policy | `AuthorizationPolicy`, the compiled rules of one principal's roles and their includes, plus the system deny rules. Immutable and shared across threads. |

The library asks the tag provider for an attribute's own tags rather than inheriting its property's, so that a deny on writing configuration need not block editing metadata an application stores as an attribute of a configuration property. A provider that wants inheritance returns the property's tags.

### Actions are names compiled to bits

The library defines `read` and `write`. Everything else (executing a method, reading history, a connector's own access kinds) is an action name the application defines and passes as the known actions of `Compile`. An enum would force every application onto the library's list or force a library change for each new kind of access; a name lets an application add `execute` or `history` without the library knowing what either means.

Names are resolved once, at compilation: `read` is bit 0, `write` bit 1, and every further known action the next bit of a `ulong`. A compiled rule keeps its actions as a mask, so the first check of every rule is one bit test. A `ulong` caps the set at 64 actions, `read` and `write` included, which is far beyond what an application defines and keeps the test a single instruction. A rule naming an action outside the known set fails compilation, so a typo cannot silently grant or deny nothing. Asking a policy about an unknown action throws, since that is a programming error of the caller, except on `DenyAll`, which answers no to everything.

### Tags come from the application

A rule selects members by meaning (state, configuration, secret, an operation that needs confirmation) through tags. The library does not define attributes for these. Code already carries semantic markers of its own, and permission attributes would duplicate them and put role decisions into libraries that cannot know the installation. `IMemberTagProvider` maps whatever an application has (attributes, registry attributes, naming conventions) to tag names, and the mapping can change without touching rules or device code.

The provider is called on every evaluation of a rule with tags, possibly concurrently, so it returns a cached array. The policy compares an array without allocating; any other collection costs an enumerator per comparison.

## Evaluation

### Allow union minus deny

The mental model is a set over the whole graph: start empty, add everything any allow rule of the principal's roles matches, remove everything any deny rule of those roles or of the system matches. What remains is allowed. `IsAllowed` computes `any allow matches && no deny matches` for the single question asked, which gives the same answer without materializing the set. Order of roles and rules does not change the answer.

A deny removes whatever it matches, wherever in the include chain it sits. A role that includes another role cannot allow back what the included role denies. This is deliberate: a precedence by include depth or by rule specificity would make a role's effect depend on who includes it, and a reader of one role could no longer tell what it guarantees. The consequence for role design is that reusable roles should mostly allow, and carve-outs belong in the role that composes them. Several roles on one principal behave exactly like one role including them all.

System deny rules use the same mechanism with one difference: they are not data the application's users edit. An application puts its invariants there, such as never revealing secrets to an agent or never letting a principal read the roles that govern it, and nothing a role says can switch them off.

### Selector order and the match context

`Matches` checks a rule's selectors cheapest first: the action bit, the member glob, the tags, the type, the path. A rule fails at the first selector that does not match, so a path is enumerated only when a rule with paths survives every other selector. Denies are evaluated only after an allow matched, which keeps a denied-by-default question cheap.

One question can test many rules, and several of them may need the same derived facts about the member. A `MatchContext` struct, passed by reference through the allow and the deny pass, computes each fact at most once per call and only when a rule needs it: the member name (which allocates for an attribute, see [Performance](#performance)), the tags from the provider, and the subject's paths. Type names are cached process-wide per runtime type, since they never change.

### Explain

`Explain(member, action)` runs the same passes and returns the first matching allow rule and the first matching deny rule with their roles. Rules are kept in closure order, included roles first, each role's rules in their written order, and the system deny rules last, so the report names the first deciding rule in that order and names `System` only when no role's deny matched. It is meant for a permission tester and for tests, not for hot paths. `Explain(subject)` is described under [Subject visibility](#subject-visibility).

## Paths

### Patterns select subjects, by prefix

A path pattern selects subjects; the `Members` selector selects members. Keeping the two apart means `/Home/Kitchen/Temperature` is never ambiguous between a child subject and a property, and each selector answers one question. A pattern matches a subject when it matches the subject's path or a prefix of it, so a rule on a subject covers its whole subtree, which is what a permission on a branch means. There is no pattern for a subject without its subtree; a rule narrows that with a type or member selector.

Patterns are parsed with the parser of the `PathProviderBase` given to `Compile`, the provider an application's tools address the graph with, so a segment means in a rule what it means in a request. Only `*` and `**` are wildcards: `*` within a segment is a glob over its text, `*` alone is one segment, `**` is any number of segments. A pattern segment without an index matches a path segment with or without one, so `/Line/Machines` covers every machine. The full table is in the README under [Path patterns](../../src/HomeBlaze/Namotion.Interceptor.Authorization/README.md#path-patterns).

A pattern names absolute paths, and every subject without parents is a root whose path is empty. The pattern `/` therefore matches every subject that has a path.

### Every path of a subject

A subject can have several parents, for example a light held both by its room and by a group of favorites. A rule for `/Rooms[Bedroom]` must apply to that light however a request reached it, or a deny could be bypassed through the other parent. The policy enumerates every path of a subject, one per chain of parents up to a root, and both kinds of rules match through any of them:

- An allow with paths matches if any path matches: the light is in an allowed branch.
- A deny with paths matches if any path matches: the light is in a denied branch, and the deny wins wherever the light is also reached.

`SubjectPaths` follows a chain of single parents without a search, which is the common case. At the first subject with several parents it switches to an iterative depth-first search over all chains, pruning a chain that returns to a subject already on it, so cycles end. Owners are not memoized, because every chain is wanted, so the search is bounded instead (see [Caps and failing closed](#caps-and-failing-closed)).

### Links carry every spelling

A path is a list of `PathLink`s from the root down to the property holding the subject. A link holds the property's segment, the index text and, for a key of an `[InlinePaths]` dictionary, the bare key when the registry's path reader resolves it without the property's segment. So `/Security/Roles[Reader]` and `/Security/Reader` name the same subject, and a pattern written in either spelling matches it. A pattern segment with an index never matches the bare spelling, since the bare spelling is a name only.

The rules for which spellings a key has follow the registry's reader: the bare key is offered when it is one non-empty name without the separator or `[`, differs from the inline property's own segment and resolves to the inline property, so it is not shadowed by another property of the same name. The explicit form is offered while the property's own segment still resolves to it. A key that resolves in neither spelling makes the path unwritable, which counts as an incomplete path set.

These rules mirror the registry inside this library, built only on the registry's public API (`PathProviderBase`, `PathExtensions.FormatPathIndex`, `InlinePathsAttribute`). That keeps the released registry package unchanged by authorization. The cost is that the mirror can drift when the registry's path rules change. The cross-check tests in `SubjectPathsTests` guard it: for plain properties, dictionary entries, collection positions, bare and shadowed inline keys, keys that need quoting and mixed paths, every combination of the spellings a link offers must resolve to the subject through the registry's `TryGetSubjectFromPath`, the spellings it leaves out must not, and the canonical spelling of the first path must equal what the registry's `TryGetPath` writes.

### Matching

`PathPattern.Matches` walks the pattern and the path together and backtracks only to the last `**` it passed. That suffices because `**` matches any number of segments, every other pattern segment matches exactly one, and a used-up pattern matches whatever path remains. Once a later `**` has matched, letting an earlier one consume more segments can only leave less path for the rest of the pattern, so retrying the last `**` with one more segment covers every alternative. The matcher has no recursion and no allocation, and its worst case is the pattern length times the path length. Segment globs use the same single-backtrack technique over characters (`Glob`).

### Caps and failing closed

A dense graph can have a number of paths exponential in its depth, so enumeration is capped: at most 64 paths, and a search budget of 64 parent links per allowed path, 4096 in all, which bounds the work in a densely connected component even when it yields no path at all. The set of paths is incomplete when:

- a property on a chain is excluded by the path provider, or an inline key resolves in neither spelling, so the chain has no path,
- the subject has parents but no chain reaches a root, as for subjects that only hold each other in a cycle,
- more than 64 paths exist, or the search budget runs out.

On an incomplete set an allow with paths matches only through the paths that were listed, and every deny with paths matches. Denies with paths fail closed: the missing path may be exactly the one the deny names, and the library cannot tell. The alternative, matching denies only against the listed paths, would let a misconfigured provider or an unusually dense graph widen a principal's permissions without any sign. The cost is that such a subject matches path denies that have nothing to do with it. The other selectors of a deny still apply, which is one more reason to give a path deny a second selector.

## Subject visibility

`CanRead(member)` answers for one member. Browsing needs a different question: whether the subject itself is visible. A subject is readable when at least one of its members is readable and some chain of parents leads from it to a root on which every subject-holding property is readable. A deny with `read` on a path therefore hides the whole branch beneath it, and an unreadable link makes the subject unreachable even though its own members are readable.

The link of a chain is a member of the parent subject, not of the child. A rule that allows reading `/Rooms[Kitchen]` makes the kitchen's members readable but not `Home.Rooms`, which belongs to the root; the kitchen stays hidden until some rule makes the links on its way readable. An application usually tags subject-holding properties so one rule grants them.

Visibility uses reachability through any parent rather than the first parent, for the same reason paths use every chain. The registry's first parent depends on attach order, which must not decide visibility, and a subject a principal can reach by browsing through readable links must not be hidden from it. The two notions do not conflict: a `read` deny on the path `/Rooms[Bedroom]` makes every member of a light there unreadable, so the light is hidden whatever other parent it has, while a deny on one link only cuts that route.

`IsReachable` walks parents depth first and stops at the first route to a root. Along a chain of single parents it allocates nothing. At a subject with several parents, or deeper than 32 levels, it adds the subject to a visited set shared by the whole walk. A subject already in the set is not explored again: its readable links and parents do not depend on how the walk arrived, so a second visit cannot find a route the first missed. Every subject with several parents, and every subject past depth 32, is therefore explored at most once, which bounds the walk and ends cycles of any shape, including two rooms that hold each other twice or three rooms that point at each other.

`Explain(subject)` returns a `SubjectReadDecision`: whether the subject is readable, whether it has a readable member at all, and otherwise the first unreadable subject-holding property the walk met, for a tester to say why a path is hidden. It reports no property when the subject is readable, when no member is readable (the cause is then on the subject itself), and when the walk met no unreadable link, as for subjects that only hold each other in a cycle with no root.

## Compilation and failure

`Compile` resolves the principal's role names, assigns the action bits, and parses every selector of every rule it will evaluate. Validation is split by what it protects:

| Validated over | Checks | Why there |
|---|---|---|
| All roles given | null, no name, a name defined twice, the reserved name `System` | Names must resolve unambiguously before any closure can be computed, and a `System` role would be indistinguishable from the system rules in `Explain`. |
| The principal's closure | unknown or empty role names, cycles of includes, includes nested more than 64 levels | Only these roles take part; a broken role outside the closure must not lock this principal out. |
| Rules of the closure and the system rules | null rules, rules without actions, unknown actions, empty selector values, malformed patterns | Same scoping: only rules the policy evaluates are parsed. |
| Known actions | an action without a name, more than 64 actions | The bit assignment depends on them. |

A principal whose roles fail to compile gets `AuthorizationPolicy.DenyAll`, never a policy built from the parts that compiled. Dropping a broken rule could drop a deny, and dropping a broken role could drop its denies, either of which widens permissions. `DenyAll` knows no actions and answers no to any action, including application actions, so a caller does not have to special-case it.

The include closure is resolved recursively; the depth cap of 64 turns a pathological include chain into a compile error with the role's name instead of a stack overflow. Roles reached through several includes, as in a diamond, are resolved once. `Roles` on the policy lists the closure in resolution order, included roles first, which is also the order of the rules and of `Explain`.

`PermissionRule` and `AuthorizationRole` are records with value equality over their arrays: element by element and in order, role names, includes, actions and tags ignoring case like resolution and matching, the other selector values exactly. An application can therefore tell whether a role really changed before recompiling, and tests can compare rules. Order counts because it decides which rule `Explain` reports.

## Performance

A policy is built for many cheap questions. The common question, a member decided by an action, member, tag or type selector, allocates nothing: rules are arrays of a sealed class, the action is a bit test, the context is a struct on the stack, tags are compared over the provider's array, and type names come from a process-wide cache.

The allocations that remain each have a reason:

- **Path rules.** When a rule with paths survives its other selectors, the subject's paths are enumerated: a list of paths, the search frames and one `PathLink` array per path. The paths are memoized for the rest of the call, and `CanRead(subject)` carries them from member to member, so a subject's paths are enumerated once per question about it and once per link on the way to a root whose evaluation reaches a path rule.
- **Attribute names.** A member glob matched against an attribute builds its `Property@Attribute` name.
- **Types.** The first type rule evaluated for a runtime type caches that type's simple and full names, its base types and its interfaces.
- **Visibility.** `CanRead(subject)` allocates the visited set when a subject on the way has several parents or the chain is deeper than 32 levels.

A deny rule with only `Paths` is reached by every member that an allow matched for that action, and proving it does not apply requires enumerating the paths. The README therefore advises giving a path deny a second selector (a type, a member glob or a tag) and putting path rules only into the roles that need them, not into a reusable base role every principal pays for.

`AuthorizationBenchmark` in `src/HomeBlaze/HomeBlaze.Benchmark` evaluates a policy of 24 rules, among them ten allow rules with paths and ten deny rules with only paths, against a graph of 50 floors with 40 lamps each (2050 subjects):

| Question | Time | Allocated |
|---|---|---|
| A read decided by a tag rule | about 23 ns | nothing |
| `CanRead(subject)`, no path rule applies to `read` | about 150 ns | nothing |
| A write where the deny rules have only path selectors and none matches | about 0.38 µs | 424 B |
| A write denied by a path rule | about 0.27 µs | 424 B |
| A browse-shaped pass: `CanRead` of every subject and of the members of the readable ones | about 0.5 ms | - |

The times were measured on a shared machine and vary between runs; the allocation column is the reliable part. The two writes allocate the same because both enumerate the lamp's single path once.

### Threading

A compiled policy holds no mutable state and is shared freely. Evaluation reads the registry's live parents and caches nothing between calls, so a question asked while the graph changes may see some links before the change and some after it, and every question asked once the change settled answers for the settled graph. A caller that wants one consistent answer for a whole pass, such as a tree browser memoizing `CanRead(subject)`, keeps that memo per call.

## Integration points

An application supplies:

- **Roles**, stored and edited wherever it keeps configuration, and the role names each principal holds. It compiles one policy per distinct set of role names, caches it, and swaps it atomically when a role or the set changes, since a policy never changes after compilation.
- **Known actions**: `read`, `write` and its own, such as `execute` for methods or `history` for recorded values.
- **A tag provider** that maps its semantic markers to tags, including a tag for subject-holding properties so that a role can grant the links visibility depends on.
- **The path provider its tools use**, so patterns mean what request paths mean.
- **System deny rules** for invariants no role may break, and `DenyAll` for any principal whose roles fail to compile.

It enforces at every entry point where a principal touches the graph: every read, write and method or operation call checks the member and the action; browsing and search check `CanRead(subject)` and leave out what is hidden, typically answering a request for a hidden member as not found so that a denial does not reveal what exists; expressions or templates evaluated on behalf of a principal resolve values through that principal's policy. An operation whose argument names another subject asks two ordinary questions, one for the operation's member and one for the target's member.

Planned follow-ups:

- An access hook in the MCP library, a small interface its tools consult per subject and member, so the tools can enforce a policy without the MCP library referencing this one.
- The application integration: role storage, the tag provider, system rules, enforcement in every tool, and operator documentation.
- A role editor with a permission tester built on `Explain`.

One question is deferred to the integration. Patterns match absolute paths, and any subject without parents is a root. A server that exposes a subtree as its root resolves request paths relative to that subtree, while rules still match from the graph's root, so such a server may need patterns that match relative to its root.

## Alternatives considered

**Path strings instead of links.** An earlier version enumerated paths as the strings the registry writes and parsed each back into segments for matching. That allocated a string and a segment list per path, and a string carries one spelling: matching both spellings of every `[InlinePaths]` key would have needed a string per combination of spellings, doubling with every such key on the path. Links carry all spellings per segment, so one path covers every combination and no string is written.

**Enumeration in the registry.** Enumerating every path is a registry concern, and an earlier version added it to the registry's public API. It moved into this library so that the released registry stays unchanged while the library incubates, and so that the spelling rules authorization needs do not become registry API for every consumer. The price is the mirrored link rules, which the cross-check tests guard.

**Fail open for incomplete path sets.** Matching denies only against the listed paths would keep an excluded property or a dense graph from denying unrelated members. It would also let exactly those cases bypass a deny, and neither is visible to the person writing rules. Failing closed makes the failure show up as a denial a tester can explain.

**A path memo per subject across calls.** Caching each subject's paths would save the enumeration on repeated questions, but needs invalidation on every structural change of any ancestor, and a stale entry could miss the deny of a branch the subject has just moved into. The memo is per call instead: per member for `IsAllowed`, and per subject across its members inside `CanRead(subject)`, where all members share the subject's paths.
