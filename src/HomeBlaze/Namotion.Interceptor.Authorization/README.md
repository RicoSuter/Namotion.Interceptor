# Namotion.Interceptor.Authorization

Role-based permissions over a subject graph: which properties a principal may read and write, which methods it may execute, and anything else an application defines as an action. Roles are data, compiled into an `AuthorizationPolicy` per principal. The library knows the graph through `Namotion.Interceptor.Registry` and nothing about any application: tags, actions beyond `read` and `write`, and where the roles come from are the application's.

## Model

A **rule** has four selectors and a list of actions. It applies to an action on a member when the action is listed and every selector that is not empty matches:

| Selector | Matches when |
|---|---|
| `Paths` | a pattern matches the member's path exactly, see [Path patterns](#path-patterns); a member of a subject reachable by several paths matches if any of them does, see [Subjects with several paths](#subjects-with-several-paths) |
| `Types` | the subject is assignable to a type or interface with that full name, or to any type with that simple name, as `Type.FullName` and `Type.Name` spell them |
| `Members` | the member's name matches a glob; `*` matches any text. An attribute is named `Property@Attribute`, an attribute of that attribute `Property@Attribute@Attribute` |
| `Tags` | the member carries every listed tag, as the application's `IMemberTagProvider` reports them |

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

A **subject** is readable (`CanRead(RegisteredSubject)`) when at least one of its members is readable and some chain of parents leads from it to a root (a subject without parents) on which every subject-holding property is readable. A deny with `read` on the property holding a subject, such as `/Home/Cameras`, therefore hides that subject and the whole branch beneath it from browsing, while its members stay readable when asked for directly; `/Home/Cameras/**` denies the members as well.

## Path patterns

Patterns are parsed with the registry's path parser of the `PathProviderBase` given to `Compile`, so a segment means what it means to that provider, quoted keys included. A pattern starts with the provider's separator. Only `*` and `**` are wildcards.

A pattern matches **member paths**, and only whole ones, like a file glob. A member's path is the path of its subject followed by the separator and the member's segment: `/Home/Kitchen/Temperature` is the member `Temperature` of the subject at `/Home/Kitchen`, and `/Volume` a member of the root. The pattern of a subject's path, `/Home/Kitchen`, therefore names the member `Kitchen` of `/Home`, the property holding the kitchen, and none of the kitchen's own members.

A member's segment is:

- For a property, its segment as the provider writes it (`TryGetPropertySegment`), or its `BrowseName` when the provider gives none, so a member the provider leaves out of paths still has a name a rule can match.
- For an attribute, the segment of the property it belongs to, `@` and the attribute's name: `Temperature@Unit`, and for an attribute of that attribute `Temperature@Unit@Source`. Since the `@` belongs to the segment, `*` matches an attribute of any level as one segment and `Temperature@*` matches every attribute of `Temperature`. An attribute also matches in the spelling the registry's paths use for it, its own segment as the provider writes it, which is the attribute's name alone (`Unit`) unless the provider maps it.

An attribute that holds subjects is a link of their paths in both spellings, so the members of a subject held by the attribute `Probe` of `Temperature` are matched as `/Home/Kitchen/Temperature@Probe/IsOn` and as `/Home/Kitchen/Probe/IsOn`.

| Pattern (separator `/`) | Matches |
|---|---|
| `/Home/Kitchen` | the member `Kitchen` of `/Home`, the property holding the kitchen; none of the kitchen's members |
| `/Home/Kitchen/Temperature` | that one member |
| `/Home/Kitchen/*` | every member of the kitchen, attributes included, but not the members of the subjects it holds; `*` alone is exactly one segment |
| `/Home/Kitchen/**` | every member of the kitchen and of every subject below it, and `Kitchen` itself, since `**` is zero or more segments |
| `/Home/**/Temperature` | every member `Temperature` at any depth under `/Home` |
| `/Home/Kitchen/Light*` | `LightA`, `Lights`, ...; `*` inside a segment is a glob over the segment text |
| `/Home/Kitchen/Temperature@*` | every attribute of `Temperature`, attributes of attributes included |
| `/Line/Machines[*]/**` | every member of every item of the collection or dictionary and below; the glob covers the index text, so `Machines[CNC*]` works |
| `/Line/Machines/**` | the same, and the member `Machines` itself: a pattern segment without an index matches a path segment with or without one |
| `/*` | every member of the root |
| `/**` | every member that has a path; unlike no `Paths`, it does not match a member of a subject none of whose paths can be listed, see [Subjects with several paths](#subjects-with-several-paths) |

A member has no index and a member path has at least one segment, so a pattern whose last segment has an index, such as `/Line/Machines[CNC1]`, and the separator alone, `/`, match no member. A rule for a branch ends in `/**`: a deny on `/Line/Machines[CNC1]/**` covers the machine's members and everything below.

- A pattern matches the spellings the path provider writes plus the bare key of an `[InlinePaths]` entry, so `/Security/FullReader/Name` and `/Security/Roles[FullReader]/Name` both match and `/Security/Roles/**` covers every key. A provider that also resolves other spellings, such as names in another case, is matched only in the spelling it writes. A key the reader only resolves in one spelling, because another property's segment shadows the other, is matched in that spelling.
- Segment text is taken as written, and a segment name or index with leading or trailing whitespace, such as `/Home/ Kitchen`, fails compilation; a quoted key may contain inner spaces, as in `/Rooms['Living Room']/*`.
- A pattern written with another provider's separator never matches: under a `.`-separated provider `.Home/Kitchen` is the single segment `Home/Kitchen`. For such a provider a pattern starts with `.`, as in `.Home.Kitchen.Temperature`.
- Quoting a key does not make `*` literal: `Machines['CNC*']` is still a glob.
- `**` with an index, as in `**[A*]`, is an ordinary glob over exactly one segment.

### Subjects with several paths

The policy enumerates every path of a member's subject, at most 64, and an allow rule with paths matches through any of them. When the paths cannot all be listed (a property on a path is excluded by the path provider, the subject has parents but no chain to a root, more than 64 paths exist, or the parents are too densely connected for the search budget), every deny rule with paths matches and allow rules match only through the paths that were listed. Denies with paths fail closed.

## Usage

```csharp
var execute = new PermissionAction("execute");
IMemberTagProvider tagProvider = new MyTagProvider();     // maps your markers to tags
PathProviderBase pathProvider = new MyPathProvider();     // the provider your paths are written with

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
        Allow = [new PermissionRule { Types = ["ILight"], Members = ["TurnOn", "Brightness"], Actions = [PermissionAction.Write, execute] }],
        Deny = [new PermissionRule { Paths = ["/Home/Bedroom/**"], Types = ["ILight"], Actions = [PermissionAction.Write, execute] }]
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

- Over all roles given: a role that is null, has no name, has a name defined more than once, or uses the reserved name `System` (`AuthorizationPolicy.SystemRoleName`).
- For the principal's roles and every role they include: an unknown or empty role name (an unknown include is reported with the including role), a cycle of includes, or includes nested more than 64 levels deep.
- For the rules of those roles and the system deny rules: a rule that is null, has no actions, names an unknown action or has an empty selector value, or a path pattern that is malformed, does not start with the provider's separator or has a segment with leading or trailing whitespace.
- For the known actions: one without a name, or more than 64 in total.

Give a principal whose roles fail to compile `AuthorizationPolicy.DenyAll`, which denies every action, known or not; a policy must never partially apply. `policy.Roles` lists the principal's roles and every role they include, included roles first.

`Explain(member, action)` returns a `PermissionDecision` naming the first allowing and the first denying role and rule, in the order of `Roles` with the system deny rules last, a system rule as the role `System`. `Explain(subject)` returns a `SubjectReadDecision`: whether the subject has a readable member and, when it is hidden although it has one, the unreadable subject-holding property on the way in `DeniedProperty`. `DeniedProperty` is null when the subject is readable and also when the walk meets no unreadable link, as for subjects that only hold each other in a cycle without a root. Both are for user interfaces and tests, not hot paths.

## Performance and threading

A compiled policy is immutable and safe to share across threads. Selectors are checked cheapest first: action, member glob, tags, type, path. A path rule is the expensive one: when one is reached for a member, the subject's paths are enumerated once per call and matched with the member's segment appended, without building path strings; `CanRead(subject)` does this once for the subject's members and once for each link on the way to a root that reaches a path rule.

A write checked against deny rules with paths is expensive although no deny rule matches: the subject's paths have to be enumerated to prove that none does. Measured numbers are in the [design](../../../docs/design/authorization.md#performance). A deny rule that has only `Paths` is evaluated against every member of every subject that gets that far, so give a path deny rule another selector (a type, a member glob or a tag) when you can, and put path rules where they are needed rather than in a reusable base role.

Apart from paths, evaluation allocates nothing, with these exceptions: matching a member glob or a path pattern against an attribute builds its `Property@Attribute` name or segment, the first evaluation of a type rule for a subject type caches the type's names, and `CanRead(subject)` allocates a visited set when a subject on the way has several parents or the chain is more than 32 levels deep.

Callers that evaluate many members of one subject, such as a tree browser, should keep their own per-call memo of `CanRead(subject)`. `IMemberTagProvider.GetTags` is called on every evaluation of a rule with tags, possibly concurrently, so it should return a cached array (another collection type costs an enumerator per comparison) and must never return null; a member without tags gets an empty array.

## JSON

A rule has its own converter, `PermissionRuleJsonConverter`, so it is written the same under any serializer options, including a resolver that leaves out properties. It is an object with the string arrays `paths`, `types`, `members` and `tags`, each written only when not empty, and `actions`, always written, with each `PermissionAction` as its name:

```json
{ "paths": ["/Home/Bedroom/**"], "types": ["ILight"], "actions": ["write", "execute"] }
```

Reading compares the property names ignoring case and reads a missing selector as empty. It throws `JsonException` for an unknown or repeated property, so a misspelled selector cannot widen an allow rule, for `null` in place of an array, and for an item that is not a string or an action name.

`AuthorizationRole` has no converter of its own: its property names are the C# names, so camelCase JSON needs `JsonSerializerOptions.Web` or other options with a camelCase naming policy; the default options expect PascalCase and compare names exactly. A missing list deserializes as empty, and a role without `name` fails to deserialize.

## Design

The design and the reasoning behind it are in [docs/design/authorization.md](../../../docs/design/authorization.md).
