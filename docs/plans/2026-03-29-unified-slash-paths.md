# Unified Slash Path Format

> **Issue:** #239 — Unify path format to slash notation with InlinePaths flattening

## Goal

Replace all dot/bracket path notation with slash (`/`) notation as the single canonical format across HomeBlaze — UI, JSON, MCP, markdown expressions, and code.

## Path Format

### Canonical Paths (JSON, UI, MCP, expressions)

| Prefix | Meaning | Example |
|--------|---------|---------|
| `/` | Absolute from root | `/Servers/OpcUaServer/Port` |
| `./` | Relative to current subject | `./Speed` |
| `../` | Navigate to parent | `../Sibling/Temperature` |

Rules:
- `/` alone references the root subject
- Empty string is never valid
- `Root` never appears in paths
- Collection/dictionary indices use brackets: `/Items[0]/Name`, `/Demo[Setup.md]`
- Inline subjects in markdown: `{{ conveyor/Temperature }}` (not dot)

### Browser Routes (URL navigation only)

Flat segments, no brackets: `/browser/Servers/0/Port`

This is a separate format used only for URL routing — not part of the canonical path system.

## API Changes

### SubjectPathResolver

Merge relative path handling from `SubjectPathResolverExtensions` into `ResolveSubject`:

```csharp
public IInterceptorSubject? ResolveSubject(
    string path,
    IInterceptorSubject? relativeTo = null)
```

- `/` or `/...` → absolute (resolve from root, `relativeTo` ignored)
- `./...` → relative to `relativeTo`
- `../...` → navigate up from `relativeTo`
- No prefix → throw (force explicit `/` or `./`)

`ResolveSubject("/")` returns the root subject directly — no consumer-side special-casing needed.

### GetPath / GetPaths

Output changes from `Child.Child` to `/Child/Child`. Slash-first caching (reverse current strategy).

### ResolveValue

`FindLastPropertySeparator` switches from dot-aware bracket-depth tracking to simple last-`/`-segment split.

Inline subject lookup in `ResolveValueFromRelativePath` changes from `conveyor.Temperature` to `conveyor/Temperature`.

### PathFormat enum

Remove or simplify — bracket format is no longer needed.

### BracketToSlash

Remove — no longer needed when slash is the only format.

## Components to Change

### Code (HomeBlaze.Services)

| File | Change |
|------|--------|
| `SubjectPathResolver.cs` | Slash-first caching, `/` = root, output paths with leading `/`, remove `BracketToSlash`, remove `JoinPathSegments` |
| `SubjectPathResolverExtensions.cs` | Merge `ResolveFromRelativePath` logic into `ResolveSubject` (`./`, `../`); update `ResolveValue`/`ResolveValueFromRelativePath` for slash format; update inline subject separator from `.` to `/` |
| `PathFormat.cs` | Remove or simplify |

### Code (Consumers)

| File | Change |
|------|--------|
| `OpcUaServer.cs` | Remove `Path == "Root"` special case — just call `_pathResolver.ResolveSubject(Path)`; update doc comment |
| `Widget.cs` | Update doc comment from `Root.folder.file.json` to `/folder/file.json` |
| `SubjectBrowser.razor` | Build paths in `/` format; rename `ObjectPath` → `Path` in `SubjectPane` class and path-building logic |
| `SubjectPropertyPanel.razor` | Rename label "ID" → "Path"; rename parameter `ObjectPath` → `Path` |
| `RenderExpression.cs` | Verify works with new format (uses `ResolveValueFromRelativePath`) |
| `NavigationItemResolver.cs` | Already uses slash — verify still works |

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
- `SubjectPathResolverRelativePathTests.cs`
- `SubjectPathResolverGetPathTests.cs`
- `SubjectPathResolverBracketToSlashTests.cs` — remove (no longer needed)
- `SubjectPathResolverCacheTests.cs`

## Migration Notes

- No backward compatibility needed — issue notes "no installations yet"
- Clean break: old bracket format is not accepted
- `PathFormat` enum can be removed entirely
