# Unified Slash Path Format

> **Issue:** #239 — Unify path format to slash notation with InlinePaths flattening

## Goal

Replace all dot/bracket path notation with slash (`/`) notation as the single canonical format across HomeBlaze — UI, JSON, MCP, markdown expressions, and code.

### Why `/` instead of `.`

Dictionary keys in `[InlinePaths]` collections often contain dots (e.g., `Setup.md`, `config.json`). Dot-separated paths require bracket escaping for these keys (`[Setup.md]`), while slash-separated paths handle them naturally (`/Setup.md`). The `/` separator eliminates this ambiguity.

The core Namotion.Interceptor library continues to use dot notation internally. The slash format is a HomeBlaze presentation concern — translation lives in `SubjectPathResolver` in HomeBlaze.Services.

## Path Format

### Canonical Paths (JSON, UI, MCP, expressions)

| Prefix | Meaning | Example |
|--------|---------|---------|
| `/` | Absolute from root | `/Servers/OpcUaServer/Port` |
| `./` | Relative (explicit) | `./Speed` |
| `../` | Navigate to parent | `../Sibling/Temperature` |
| No prefix | Relative (implicit) | `motor/Speed` |

Rules:
- `/` alone references the root subject
- Empty string is never valid
- `Root` never appears in paths
- `[InlinePaths]` dictionary keys become path segments directly: `/Demo/Setup.md/Temperature`
- Regular (non-InlinePaths) collection/dictionary indices use brackets on the property: `/Items[0]/Name`, `/Children[key]/Name`
- `../` with multiple parents returns null (ambiguous, cannot decide)
- Inline subjects in markdown: `{{ motor/Speed }}` — resolved as a normal relative path via `[InlinePaths]`, no special lookup needed

### Browser Routes (URL navigation only)

Flat segments, no brackets: `/browser/Servers/0/Port`

This is a separate format used only for URL routing — not part of the canonical path system.

## API Changes

### PathStyle enum (replaces PathFormat)

```csharp
public enum PathStyle
{
    Canonical,  // /Items[0]/Name - brackets for non-InlinePaths indices
    Route       // /Items/0/Name  - flat segments, no brackets (URL routing only)
}
```

Replaces the old `PathFormat` enum (Bracket/Slash).

### SubjectPathResolver

**`ResolveSubject`** — unified resolution, handles all prefix styles:

```csharp
public IInterceptorSubject? ResolveSubject(
    string path,
    PathStyle style,
    IInterceptorSubject? relativeTo = null)
```

- `/` or `/...` → absolute (resolve from root, `relativeTo` ignored)
- `./...` → relative to `relativeTo`
- `../...` → navigate up from `relativeTo`, null if multiple parents
- No prefix → relative to `relativeTo`

`ResolveSubject("/")` returns the root subject directly.

Resolution needs `PathStyle` to correctly interpret ambiguous segments (e.g., is `0` a dictionary key or a property name?).

**`GetPath` / `GetPaths`** — output with style:

```csharp
public string? GetPath(IInterceptorSubject subject, PathStyle style)
public IReadOnlyList<string> GetPaths(IInterceptorSubject subject, PathStyle style)
```

Output changes from `Child.Child` to `/Child/Child`. Slash-first caching (reverse current strategy).

### ResolveValue (extension method)

Unified value resolution replacing `ResolveValue`, `ResolveValueFromRelativePath`, and `ResolveFromRelativePath`:

```csharp
public static object? ResolveValue(
    this SubjectPathResolver resolver,
    string path,
    PathStyle style,
    IInterceptorSubject? relativeTo = null)
```

- Split on last `/` → subject path + property name
- Call `ResolveSubject` for the subject part
- Return property value or null

The `inlineSubjects` parameter is eliminated — `[InlinePaths]` navigation handles inline subject lookup through normal path resolution.

`FindLastPropertySeparator` simplifies to finding the last `/` (no bracket-depth tracking needed in canonical style, though brackets on property names like `Items[0]` still need consideration).

### Removed

- `SubjectPathResolverExtensions.ResolveFromRelativePath` → merged into `ResolveSubject`
- `SubjectPathResolverExtensions.ResolveValueFromRelativePath` → merged into `ResolveValue`
- `PathFormat` enum → replaced by `PathStyle`
- `BracketToSlash` → no longer needed
- `JoinPathSegments` → no longer needed
- `inlineSubjects` parameter on `ResolveValueFromRelativePath` → `[InlinePaths]` handles it

## Components to Change

### Code (HomeBlaze.Services)

| File | Change |
|------|--------|
| `SubjectPathResolver.cs` | Slash-first caching, `/` = root, output paths with leading `/`, remove `BracketToSlash`, remove `JoinPathSegments`, accept `PathStyle` parameter |
| `SubjectPathResolverExtensions.cs` | Collapse to single `ResolveValue` extension; remove `ResolveFromRelativePath`, `ResolveValueFromRelativePath`, `inlineSubjects` parameter; simplify `FindLastPropertySeparator` |
| `PathFormat.cs` | Replace with `PathStyle` enum (Canonical/Route) |

### Code (Consumers)

| File | Change |
|------|--------|
| `OpcUaServer.cs` | Remove `Path == "Root"` special case — just call `_pathResolver.ResolveSubject(Path, PathStyle.Canonical)`; update doc comment |
| `Widget.cs` | Update to use `ResolveSubject` with `PathStyle.Canonical`; update doc comment from `Root.folder.file.json` to `/folder/file.json` |
| `SubjectBrowser.razor` | Build paths in `/` format with `PathStyle.Route`; rename `ObjectPath` → `Path` in `SubjectPane` class and path-building logic |
| `SubjectPropertyPanel.razor` | Rename label "ID" → "Path"; rename parameter `ObjectPath` → `Path` |
| `RenderExpression.cs` | Simplify to use `ResolveValue` with `PathStyle.Canonical` and `relativeTo: Parent` — no special inline subject handling |
| `NavigationItemResolver.cs` | Already uses slash — update to `PathStyle.Route` |

### MCP Tools

Prepend `/` to output paths, strip leading `/` on input for core library compatibility.

### Data Files

| File | Change |
|------|--------|
| `Data/Servers/OpcUaServer.json` | `"path": "Root"` → `"path": "/"` |
| `Data/Demo/Setup.md` | All `{{ Root.Children[Demo].Children[X].Prop }}` → `{{ /Demo/X/Prop }}` |
| `Data/Demo/Inline.md` | `"path": "Root.Demo.Conveyor"` → `"path": "/Demo/Conveyor"` |

### Documentation (HomeBlaze/Data/Docs)

| File | Sections to update |
|------|-------------------|
| `glossary.md` | Paths section — rewrite with `/`, `./`, `../` format |
| `administration/configuration.md` | Path syntax section, all path examples, JSON reference examples |
| `development/building-subjects.md` | Path reference table, code examples |
| `development/pages.md` | Expression path examples |
| `architecture/design/ai.md` | Add leading `/` to MCP path example |
| `architecture/design/scalability.md` | Path example in caching discussion |
| `plans/ai-agents.md` | Path examples in JSON config |

### Tests (HomeBlaze.Services.Tests)

All 5 test files need rewriting for new format:
- `SubjectPathResolverResolveSubjectTests.cs`
- `SubjectPathResolverRelativePathTests.cs` — may merge into ResolveSubject tests
- `SubjectPathResolverGetPathTests.cs`
- `SubjectPathResolverBracketToSlashTests.cs` — remove (no longer needed)
- `SubjectPathResolverCacheTests.cs`

## Migration Notes

- No backward compatibility needed — issue notes "no installations yet"
- Clean break: old bracket/dot format is not accepted
- `PathFormat` enum removed entirely, replaced by `PathStyle`
- Core Namotion.Interceptor library unchanged — dot notation remains internal
