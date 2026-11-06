# Authorization

The `Namotion.Interceptor.Authorization` library decides role-based permissions over a subject graph: which properties a principal may read and write, which methods it may execute, and anything else an application defines as an action. Roles are data, compiled into an `AuthorizationPolicy` per principal. The library knows the graph through `Namotion.Interceptor.Registry` and nothing about any application: tags, actions beyond `read` and `write`, and where the roles come from are the application's. The reasoning behind the design is in [Authorization design](design/authorization.md).

## Model

A **rule** has four selectors and a list of actions. It applies to an action on a member when the action is listed and every selector that is not empty matches:

| Selector | Matches when |
|---|---|
| `Paths` | a pattern matches the member's path exactly, see [Path patterns](#path-patterns); a member of a subject reachable by several paths matches if any of them does, see [Subjects with several paths](#subjects-with-several-paths) |
| `Types` | the subject is assignable to a type or interface with that full name, or to any type with that simple name, as `Type.FullName` and `Type.Name` spell them |
| `Members` | the member's name matches a glob; `*` matches any text. An attribute is named `Property@Attribute`, an attribute of that attribute `Property@Attribute@Attribute`. An attribute whose property is missing, or whose attributes name each other in a cycle, has no name: a deny with `Members` matches it and an allow with `Members` does not |
| `Tags` | the member carries every listed tag, as the application's `IMemberTagProvider` reports them |

A simple type name matches a type of that name in any namespace, so an allow rule should name types by their full name, which another library is unlikely to reuse.

A **member** is any registered property of a subject: a property, a method an application registers as a property, or an attribute, which is evaluated on its own with its own tags.

A **role** has a name, the names of roles it includes, `Allow` rules and `Deny` rules. Its effective rules are its own and those of every included role, transitively.

An **action** is a name. `PermissionAction.Read` and `PermissionAction.Write` are built in; an application defines others, such as `execute` or `history`, and passes every action a rule may name as the known actions of `Compile`. A rule naming an unknown action fails compilation. At most 64 actions are supported, `read` and `write` included.

Action names, role names and tags compare ignoring case. Type names, member globs and path patterns compare exactly.

## How allow and deny combine

For one principal, conceptually over the whole graph:

1. Start empty: nothing is allowed.
2. Add everything every allow rule of the principal's roles matches. Order does not matter; it is a union.
3. Remove everything any deny rule of those roles, or any system deny rule of the application, matches. A deny removes what it matches whatever role allowed it.

What is left is allowed. `IsAllowed` computes `any allow matches && no deny matches` per question, which gives the same answer without materializing the set. Consequences:

- A role cannot undo a deny of a role it includes; carve-outs belong in the including role, reusable roles should mostly allow.
- Naming several roles for a principal is the same as one role that includes them all.
- No matching allow means denied.

A **subject** is readable (`CanRead(RegisteredSubject)`) when at least one of its members is readable and some chain of parents leads from it to a root (see [Subjects with several paths](#subjects-with-several-paths)) on which every subject-holding property is readable. A deny with `read` on a subject-holding property, such as `Home.Cameras`, therefore hides the subjects it holds and every branch beneath them from browsing, while their members stay readable when asked for directly; `Home.Cameras.**` denies the members as well. The link is the property itself: a pattern naming one entry of a collection or dictionary, such as `Home.Rooms[Kitchen]`, names no member and fails compilation (see [Path patterns](#path-patterns)), so hiding one entry takes `Home.Rooms[Kitchen].**`, which denies the entry's members and hides the entry with them.

## Path patterns

Patterns are parsed with the registry's path parser of the `PathProviderBase` given to `Compile`, so a segment means what it means to that provider, quoted keys included. The examples use the registry's default provider, whose separator is `.`; a provider with another separator, such as `/`, works the same way with its own separator. Only `*` and `**` are wildcards.

A pattern names paths from the root and may start with the separator or not: `Home.Kitchen.Temperature` and `.Home.Kitchen.Temperature` are the same pattern, so the path of a member that the registry's `TryGetPath` writes usually compiles as a pattern for that member. Exceptions include a segment or key with leading or trailing whitespace or a control or format character, which fails compilation, a key containing `*`, which is read as a glob, and a path that opens with a bare `[InlinePaths]` key starting with `/` or `\`, which fails as described below. The path of a subject held by a collection or dictionary ends in an index, names no member and fails compilation too.

A pattern matches **member paths**, and only whole ones, like a file glob. A member's path is the path of its subject followed by the separator and the member's segment: `Home.Kitchen.Temperature` is the member `Temperature` of the subject at `Home.Kitchen`, and `Volume` a member of the root. The pattern of a subject's path, `Home.Kitchen`, therefore names the member `Kitchen` of `Home`, the property holding the kitchen, and none of the kitchen's own members.

A member's segment is:

- For a property, its segment as the provider spells it (`TryGetPropertySegment`), or its `BrowseName` when the provider excludes it, so a member the provider leaves out of paths still has a name a rule can match. This widens allow rules too: when a provider maps one property's segment to `Secret` and excludes another property whose `BrowseName` is `Secret`, an allow on `Secret` grants both, and any allow pattern that matches an excluded member's `BrowseName`, such as `*`, `**` or a segment glob, grants that member.
- For an attribute, the same, so attribute paths follow the registry's path syntax, which spells an attribute by its name alone unless the provider maps it: the attribute `Unit` of `Temperature` is `Home.Kitchen.Unit`, an attribute `Source` of that attribute `Home.Kitchen.Source`, and the members of a subject held by an attribute `Probe` are `Home.Kitchen.Probe.IsOn`. Attributes of the same name on different properties of one subject therefore share a path, and a deny on `Home.Kitchen.Temperature.**` covers `Temperature` and the subjects it holds but none of its attributes. The name `Temperature@Unit` works only in `Members`; as a path segment it names no member. As in the registry, a path passes an attribute only while the provider includes the attribute and the property it belongs to, recursively (`PathProviderExtensions.IsPropertyEffectivelyIncluded`), so when `Temperature` is excluded, the subject held by `Probe` has no path; see [Subjects with several paths](#subjects-with-several-paths).

| Pattern | Matches |
|---|---|
| `Home.Kitchen` | the member `Kitchen` of `Home`, the property holding the kitchen; none of the kitchen's members |
| `Home.Kitchen.Temperature` | that one member |
| `Home.Kitchen.*` | every member of the kitchen, attributes of every level included, but not the members of the subjects it holds; `*` alone is exactly one segment |
| `Home.Kitchen.**` | every member of the kitchen and of every subject below it, and `Kitchen` itself, since `**` is zero or more segments |
| `Home.**.Temperature` | every member `Temperature` at any depth under `Home` |
| `Home.Kitchen.Light*` | `LightA`, `Lights`, ...; `*` inside a segment is a glob over the segment text |
| `Line.Machines[*].**` | every member of every item of the collection or dictionary and below; the glob covers the index text, so `Machines[CNC*]` works |
| `Line.Machines.**` | the same, and the member `Machines` itself: a pattern segment without an index matches a path segment with or without one |
| `*` | every member of the root |
| `**` | every member of every subject that has a path, members the provider excludes included, through their `BrowseName`; unlike no `Paths`, an allow with it does not match a member of a subject none of whose paths can be listed, see [Subjects with several paths](#subjects-with-several-paths) |

A member has no index and a member path has at least one segment, so a pattern whose last segment has an index, such as `Line.Machines[CNC1]`, and the separator alone, `.`, would match no member; they fail compilation, so that a deny cannot silently match nothing. A rule for a branch ends in `.**`: a deny on `Line.Machines[CNC1].**` covers the machine's members and everything below, and `Line.Machines[CNC1].*` only the machine's own members.

Compilation cannot catch every pattern that matches nothing, because it does not see the graph. Besides a misspelled name, the case to know is the bare key of an `[InlinePaths]` entry as the last segment. The registry builds the path of the subject at `Security.Roles[Reader]` as `Security.Reader`, but the member holding that subject is `Roles`, so the pattern `Security.Reader` names no member: it compiles, matches nothing, and as a deny hides nothing. Write `Security.Reader.**` for the subject and everything below it. An application that lets people write patterns can check each pattern against its graph and warn about one that matches no member.

- A pattern matches the spellings the path provider builds plus the bare key of an `[InlinePaths]` entry, so `Security.FullReader.Name` and `Security.Roles[FullReader].Name` both match and `Security.Roles.**` covers every key. A provider that also resolves other spellings, such as names in another case, is matched only in the spelling it builds. A deny matches every structural spelling of a key, the explicit form always and the bare key whenever it is one non-empty name without the separator or the index-open character that differs from the property's own segment, whether or not a property shadows that spelling: a deny on `Security.Reader.**` also covers `Security.Roles[Reader]` while a declared property `Reader` of `Security` shadows the bare key, and keeps applying when such a property is added later. An allow matches only the spellings that resolve.
- Segment text is taken as written, and a segment name or index with leading or trailing whitespace, such as `Home. Kitchen`, fails compilation; a quoted key may contain inner spaces, as in `Rooms['Living Room'].*`.
- An empty segment, from a doubled or trailing separator as in `Home..Kitchen` or `Home.Kitchen.`, fails compilation, since the parser would otherwise skip it.
- A pattern that starts with `/` or `\` when that is not the provider's separator, such as `/Home/Kitchen/**` under the default provider, fails compilation with a hint naming the provider's separator. Elsewhere in a pattern these characters are ordinary text, as they can be in an `[InlinePaths]` key, so `Home/Kitchen` without the leading character is the single segment `Home/Kitchen` and matches no member. A bare `[InlinePaths]` key that starts with `/` or `\` therefore cannot open a pattern; write it in the explicit form, as in `Areas[/Garage].**`. The same holds for any pattern in another provider's style that does not start with `/` or `\`: under a provider whose separator is `/`, `Rooms.**` compiles as the single segment `Rooms.**` and matches none of the members it was written for.
- Quoting a key does not make `*` literal: `Machines['CNC*']` is still a glob.
- `**` with an index, as in `**[A*]`, is an ordinary glob over exactly one segment.

### Subjects with several paths

The policy enumerates every path of a member's subject, at most 64, and an allow rule with paths matches through any of them. When the paths cannot all be listed, every deny rule with paths matches and allow rules match only through the paths that were listed, so denies with paths fail closed. The paths cannot all be listed when:

- a path passes a property the path provider excludes, or an attribute of an excluded property, through which the registry builds and resolves no path either;
- a path passes an `[InlinePaths]` key that resolves neither bare nor in the explicit form, or an `[InlinePaths]` property without an index whose own segment another property shadows, for which the registry builds no path either;
- the subject has parents but no chain to a root;
- the subject or one on a chain is no longer attached because it is being detached or was detached;
- the graph changed during every attempt to list the paths as of one instant, in which case no path is listed at all;
- more than 64 paths exist; or
- the parents are too densely connected for the search budget of 64 parent links per allowed path.

A path deny that fails closed this way still needs its other selectors to match, so giving it one, such as a type or a tag, keeps it from denying members it was not written for.

A root is a subject without parents that the registry still counts as attached (`RegisteredSubject.IsAttached`); the paths of its members have one segment. A subject being detached, a subject below it, and a detached subject whose member a caller still holds are never taken for a root: a question asked during or after the detach sees such a subject either on its old path or with paths that cannot be listed, and a path deny applies either way. The [design](design/authorization.md#patterns-match-member-paths-exactly) explains how the registry's attachment guarantees this. A root assigned into its own graph has a parent and is no root any more, and no other subject becomes one: every path is incomplete, so allow rules with paths match nothing, deny rules with paths match everything and no subject is readable. That fails closed and costs only availability.

### Known limits

- A subject held more than once in one collection or dictionary, such as one light at two keys, has a single parent in the registry, at the first position or key. Its members match only through that path, so a deny written for the other key does not apply to them. This is a limitation of the registry and is to be fixed there; until then, deny such a subject with a pattern that covers both keys, such as `Home.Kitchen.Lights.**`, or with another selector.
- A deny with paths applies where a subject is now. A principal allowed to change the structure of the graph, by moving a subject, re-keying a dictionary entry, reordering a collection or adding a subject a second time to a collection, can take a subject out of a denied branch. A deny with paths is reliable only while such writes are restricted, so select secrets with `Types`, `Tags` or `Members`, which do not depend on where a subject is.

## Usage

```csharp
var execute = new PermissionAction("execute");
IMemberTagProvider tagProvider = new MyTagProvider();       // maps your markers to tags
PathProviderBase pathProvider = DefaultPathProvider.Instance; // the provider your paths are built with

var roles = new[]
{
    new AuthorizationRole
    {
        Name = "Reader",
        Allow = [new PermissionRule { Tags = ["state"], Actions = [PermissionAction.Read] }]
    },
    new AuthorizationRole
    {
        Name = "LightOperator",
        Includes = ["Reader"],
        Allow = [new PermissionRule { Types = ["MyApp.Devices.ILight"], Members = ["TurnOn", "Brightness"], Actions = [PermissionAction.Write, execute] }],
        Deny = [new PermissionRule { Paths = ["Home.Bedroom.**"], Types = ["ILight"], Actions = [PermissionAction.Write, execute] }]
    }
};

var systemDeny = ImmutableArray.Create(new PermissionRule { Tags = ["secret"], Actions = [PermissionAction.Read, PermissionAction.Write] });

var policy = AuthorizationPolicy.Compile(
    roles, roleNames: ["LightOperator"], systemDeny,
    knownActions: [PermissionAction.Read, PermissionAction.Write, execute],
    tagProvider, pathProvider);

policy.CanRead(member);              // read
policy.IsAllowed(member, execute);   // any known action
policy.CanRead(registeredSubject);   // visible at all
```

`IsAllowed` and `Explain(member, action)` throw `ArgumentException` for an action that was not among the known actions of `Compile`.

`Compile` throws `AuthorizationException` when the roles cannot be compiled; the message names the role wherever there is one:

- Over all roles given: a role that is null, has no name, has a name with leading or trailing whitespace or a control or format character, has a name defined more than once, or uses the reserved name `System` (`AuthorizationPolicy.SystemRoleName`).
- For the principal's roles and every role they include: an unknown or empty role name or one with leading or trailing whitespace or a control or format character (an include is reported with the including role), a cycle of includes, or includes nested more than 64 levels deep.
- For the rules of those roles and the system deny rules: a rule that is null, has no actions, names an unknown action or has a selector value that is empty, has leading or trailing whitespace or contains a control or format character, or a path pattern that is malformed, starts with `/` or `\` that is not the provider's separator, has an empty segment, has a segment with leading or trailing whitespace, or matches no member because it is the separator alone or its last segment has an index.
- For the known actions: one without a name, or more than 64 in total.

Give a principal whose roles fail to compile `AuthorizationPolicy.DenyAll`, which denies every action, known or not; a policy must never partially apply. `policy.Roles` lists the principal's roles and every role they include, included roles first.

`Explain(member, action)` returns a `PermissionDecision` naming the first allowing and the first denying role and rule, in the order of `Roles` with the system deny rules last, a system rule as the role `System`. `Explain(subject)` returns a `SubjectReadDecision`: whether the subject has a readable member and, when it is hidden although it has one, the unreadable subject-holding property on the way in `DeniedProperty`. `DeniedProperty` is null when the subject is readable and also when the walk meets no unreadable link, as for subjects that only hold each other in a cycle without a root. Both are for user interfaces and tests, not hot paths. They return whole roles and rules and the live subject-holding property, so show them only to principals who may see the policy and the graph.

## Performance and threading

A compiled policy is immutable and safe to share across threads. Every answer rests on the parents and attachment of one instant of the graph, also while subjects move, and an answer whose parents kept changing fails closed. Tags, member segments and which `[InlinePaths]` spellings resolve are read live, which allows the one exception described under [Threading](design/authorization.md#threading). The common questions allocate nothing, path rules over a chain of single parents included; the exceptions and the measured allocations are in the [design](design/authorization.md#performance). Path rules are the expensive selector: a deny rule with only `Paths` makes every member an allow matched enumerate its subject's paths to prove the deny does not apply. Give a path deny rule another selector (a type, a member glob or a tag) when you can, and put path rules into the roles that need them rather than into a reusable base role. Callers that evaluate many members of one subject, such as a tree browser, should keep their own per-call memo of `CanRead(subject)`. `IMemberTagProvider.GetTags` is called on every evaluation of a rule with tags, possibly concurrently, so it should return cached arrays; it must never return a default array, for which evaluation throws `InvalidOperationException` naming the provider.

## JSON

A rule has its own converter, `PermissionRuleJsonConverter`, so it is written the same under any serializer options, including a resolver that leaves out properties. It is an object with the string arrays `paths`, `types`, `members` and `tags`, each written only when not empty, and `actions`, always written, with each `PermissionAction` as its name:

```json
{ "paths": ["Home.Bedroom.**"], "types": ["ILight"], "actions": ["write", "execute"] }
```

Reading compares the property names ignoring case and reads a missing selector as empty. It throws `JsonException` for an unknown or repeated property, so a misspelled selector cannot widen an allow rule, for `null` in place of an array, and for an item that is not a string or an action name.

`AuthorizationRole` has no converter of its own: its property names are the C# names, so camelCase JSON needs `JsonSerializerOptions.Web` or other options with a camelCase naming policy; the default options expect PascalCase and compare names exactly. A missing list deserializes as empty, with reflection and with a source-generated context alike. Deserializing throws `JsonException` for an unknown property, so a misspelled `deny` cannot drop a role's deny rules, for `null` in place of a list, and for a role without `name`.
