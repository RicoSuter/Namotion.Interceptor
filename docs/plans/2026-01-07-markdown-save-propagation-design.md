# Markdown Save Propagation Design

## Problem

When editing nested subjects (e.g., CanvasNode, GridCell) embedded in a MarkdownFile, changes don't persist because:

1. `SubjectEditPanel.SaveAsync()` finds the nearest `IConfigurationWriter` in the parent chain
2. MarkdownFile doesn't implement `IConfigurationWriter`
3. Falls through to RootManager which writes JSON files, not markdown

## Solution

Implement chain-of-responsibility pattern where MarkdownFile participates in the save chain:

1. MarkdownFile implements `IConfigurationWriter`
2. When called, serializes all embedded subjects back to markdown
3. Delegates to its parent writer (storage container)

## Components

### 1. TryFindFirstParent Extension

Add to `Namotion.Interceptor.Tracking/Parent/ParentsHandlerExtensions.cs`:

```csharp
/// <summary>
/// Finds the first ancestor of the specified type by traversing the parent hierarchy (BFS).
/// </summary>
/// <typeparam name="T">The type to search for.</typeparam>
/// <param name="subject">The subject to start searching from.</param>
/// <returns>The first matching ancestor, or null if not found.</returns>
public static T? TryFindFirstParent<T>(this IInterceptorSubject subject)
    where T : class
{
    var visited = new HashSet<IInterceptorSubject>();
    var queue = new Queue<IInterceptorSubject>();
    queue.Enqueue(subject);

    while (queue.Count > 0)
    {
        var current = queue.Dequeue();

        if (!visited.Add(current))
        {
            continue;
        }

        // Don't match the starting subject itself
        if (current is T match && !ReferenceEquals(current, subject))
        {
            return match;
        }

        foreach (var parent in current.GetParents())
        {
            if (!parent.Equals(default))
            {
                queue.Enqueue(parent.Property.Subject);
            }
        }
    }

    return null;
}
```

### 2. MarkdownFile Changes

Update `HomeBlaze.Storage/Files/MarkdownFile.cs`:

```csharp
public partial class MarkdownFile : IStorageFile, ITitleProvider, IIconProvider, IPage, IConfigurationWriter
{
    private readonly ConfigurableSubjectSerializer _serializer;

    // Constructor updated to accept serializer
    public MarkdownFile(
        IStorageContainer storage,
        string fullPath,
        MarkdownContentParser parser,
        ConfigurableSubjectSerializer serializer)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        Children = new Dictionary<string, IInterceptorSubject>();
        _parser = parser;
        _serializer = serializer;
    }

    public async Task<bool> WriteConfigurationAsync(
        IInterceptorSubject subject,
        CancellationToken cancellationToken)
    {
        // Rebuild markdown with all embedded subjects serialized
        Content = RebuildMarkdownContent();

        // Continue chain to parent (storage container)
        var parentWriter = ((IInterceptorSubject)this).TryFindFirstParent<IConfigurationWriter>();
        if (parentWriter != null)
        {
            await parentWriter.WriteConfigurationAsync(this, cancellationToken);
        }

        return true;
    }

    private string RebuildMarkdownContent()
    {
        if (string.IsNullOrEmpty(Content))
        {
            return string.Empty;
        }

        // Regex matches: ```subject(name)\n{json}```
        return Regex.Replace(
            Content,
            @"```subject\(([^)]+)\)\s*\n[\s\S]*?```",
            match =>
            {
                var name = match.Groups[1].Value;

                // Find the child subject by name and serialize it
                if (Children.TryGetValue(name, out var child))
                {
                    var json = _serializer.Serialize(child);
                    return $"```subject({name})\n{json}\n```";
                }

                // Subject not found - keep original block unchanged
                return match.Value;
            });
    }
}
```

### 3. SubjectEditPanel Refactor

Update `HomeBlaze.Components/Editors/SubjectEditPanel.razor`:

Replace the local `FindNearest<T>` method with the new extension:

```csharp
// Change from:
var configurationWriter = FindNearest<IConfigurationWriter>(subject) ?? RootManager;

// To:
var configurationWriter = subject.TryFindFirstParent<IConfigurationWriter>() ?? RootManager;

// Delete the local FindNearest<T> method entirely
```

## Data Flow

```
Edit CanvasNode -> Save dialog closes
  -> SubjectEditPanel.SaveAsync()
  -> subject.TryFindFirstParent<IConfigurationWriter>()
  -> finds MarkdownFile
  -> MarkdownFile.WriteConfigurationAsync(subject)
    -> RebuildMarkdownContent() - serialize ALL children to markdown
    -> TryFindFirstParent<IConfigurationWriter>()
    -> finds Storage (IStorageContainer)
    -> Storage.WriteConfigurationAsync(this)
    -> writes file to disk
```

## Files Modified

| File | Change |
|------|--------|
| `Namotion.Interceptor.Tracking/Parent/ParentsHandlerExtensions.cs` | Add `TryFindFirstParent<T>()` |
| `HomeBlaze.Storage/Files/MarkdownFile.cs` | Implement `IConfigurationWriter`, add `RebuildMarkdownContent()`, inject serializer |
| `HomeBlaze.Components/Editors/SubjectEditPanel.razor` | Use `TryFindFirstParent<T>`, remove local `FindNearest<T>` |

## Trigger

Auto-save on dialog close (already implemented in SubjectEditDialog).
