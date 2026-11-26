using System.Collections.Immutable;

namespace HomeBlaze.Virtual;

/// <summary>Checks the properties of a virtual subject; the editor and the runtime use the same rules.</summary>
public static class VirtualPropertyValidator
{
    private const string CycleMessage = "The entry reads itself through other entries.";

    // The properties that VirtualSubject declares, which an entry of its own cannot be named after.
    private static readonly string[] ReservedRootNames = ["Title", "Properties", "Status", "StatusMessage", "IconColor"];

    /// <summary>
    /// Gets the issues of a tree: each invalid entry, without the entries below it, and each entry that reads itself
    /// through other entries of the same subject.
    /// </summary>
    /// <param name="properties">The entries of the virtual subject.</param>
    /// <param name="ownPath">The canonical path of the virtual subject, to follow absolute paths into it; null when it has none.</param>
    public static ImmutableArray<VirtualPropertyIssue> Validate(IReadOnlyDictionary<string, VirtualProperty>? properties, string? ownPath = null)
    {
        var issues = ImmutableArray.CreateBuilder<VirtualPropertyIssue>();
        var computed = new Dictionary<string, ComputedEntry>(StringComparer.Ordinal);
        Walk(properties, VirtualEntryPlace.Root, parentPath: null, level: [], '/', issues, computed);
        foreach (var path in FindCycles(computed, ownPath))
        {
            issues.Add(new VirtualPropertyIssue(path, CycleMessage));
        }

        return issues.ToImmutable();
    }

    /// <summary>Checks one entry, not the entries below it; null when it is valid, otherwise the message of the issue.</summary>
    internal static string? Check(string name, VirtualProperty property, VirtualEntryPlace place)
    {
        if (!IsIdentifier(name))
        {
            return "A name has letters, digits and underscores only and does not start with a digit.";
        }

        if (place == VirtualEntryPlace.Root && Array.IndexOf(ReservedRootNames, name) >= 0)
        {
            return $"The name {name} is taken by a property of the virtual subject.";
        }

        var sourceCount = (property.Expression is null ? 0 : 1) + (property.Path is null ? 0 : 1) + (property.Properties is null ? 0 : 1);
        if (sourceCount != 1)
        {
            return "An entry has exactly one of expression, path or properties.";
        }

        return property.Properties is null ? CheckValueEntry(property) : CheckNestedEntry(property, place);
    }

    private static string? CheckNestedEntry(VirtualProperty property, VirtualEntryPlace place)
    {
        if (place == VirtualEntryPlace.Attribute)
        {
            return "An attribute cannot hold properties.";
        }

        return property.DataType is null && property.State is null && property.Attributes is null
            ? null
            : "A nested subject has no data type, state or attributes.";
    }

    private static string? CheckValueEntry(VirtualProperty property)
    {
        if (property.DataType is not { } dataType)
        {
            return "A data type is required.";
        }

        if (!Enum.IsDefined(dataType))
        {
            return "The data type is not known.";
        }

        if (property.Path is { } path && string.IsNullOrWhiteSpace(path))
        {
            return "A path is required.";
        }

        if (property.Expression is not { } text)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "An expression is required.";
        }

        return VirtualExpression.TryParse(text, out _, out var error) ? null : error;
    }

    // Collects the issues of the entries and the valid expression and binding entries by their path in the tree; an
    // attribute's path joins its property's path with '@', as the registry names attribute properties.
    private static void Walk(
        IReadOnlyDictionary<string, VirtualProperty>? entries,
        VirtualEntryPlace place,
        string? parentPath,
        string[] level,
        char separator,
        ImmutableArray<VirtualPropertyIssue>.Builder issues,
        Dictionary<string, ComputedEntry> computed)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var (name, property) in entries)
        {
            var path = parentPath is null ? name : parentPath + separator + name;
            if (Check(name, property, place) is { } message)
            {
                issues.Add(new VirtualPropertyIssue(path, message));
                continue;
            }

            if (property.Properties is { } children)
            {
                Walk(children, VirtualEntryPlace.Nested, path, [.. level, name], '/', issues, computed);
                continue;
            }

            // An attribute is on the same subject as its property, so its relative paths start at the same level.
            computed[path] = new ComputedEntry(level, GetReadPaths(property));
            Walk(property.Attributes, VirtualEntryPlace.Attribute, path, level, '@', issues, computed);
        }
    }

    private static string[] GetReadPaths(VirtualProperty property)
    {
        if (property.Path is { } path)
        {
            return [path];
        }

        return property.Expression is { } text && VirtualExpression.TryParse(text, out var expression, out _) ? [.. expression.Paths] : [];
    }

    private static IEnumerable<string> FindCycles(Dictionary<string, ComputedEntry> computed, string? ownPath)
    {
        var edges = computed.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.ReadPaths
                .Select(path => ToTreePath(path, entry.Value.Level, ownPath))
                .OfType<string>()
                .Where(computed.ContainsKey)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);

        return computed.Keys.Where(path => Reaches(edges, path, path));
    }

    private static bool Reaches(Dictionary<string, string[]> edges, string from, string target)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(edges[from]);
        while (pending.TryPop(out var current))
        {
            if (current == target)
            {
                return true;
            }

            if (visited.Add(current))
            {
                foreach (var next in edges[current])
                {
                    pending.Push(next);
                }
            }
        }

        return false;
    }

    // The path in the tree that a read path leads to inside the same virtual subject, or null when it leads outside.
    // With the subject's own path known, any read path is made absolute first, so that a relative path that leaves the
    // subject and comes back counts too; without it, only a relative path that stays inside counts.
    private static string? ToTreePath(string path, string[] level, string? ownPath)
    {
        if (ownPath is null)
        {
            List<string> relative = [.. level];
            return !path.StartsWith('/') && AppendSegments(relative, path) ? JoinTreePath(relative, 0) : null;
        }

        var ownSegments = ownPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> segments = path.StartsWith('/') ? [] : [.. ownSegments, .. level];
        if (!AppendSegments(segments, path) || segments.Count <= ownSegments.Length)
        {
            return null;
        }

        for (var index = 0; index < ownSegments.Length; index++)
        {
            if (!string.Equals(segments[index], ownSegments[index], StringComparison.Ordinal))
            {
                return null;
            }
        }

        return JoinTreePath(segments, ownSegments.Length);
    }

    // Applies the segments of a path to the given ones; false when it goes above the first of them. Repeated separators
    // lead where one does, as the runtime skips them.
    private static bool AppendSegments(List<string> segments, string path)
    {
        foreach (var segment in path.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return false;
                }

                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }

        return true;
    }

    // The segments from the start index as a path in the tree; null when there are none or one leads into a
    // collection, which a virtual subject does not have.
    private static string? JoinTreePath(List<string> segments, int start)
    {
        if (segments.Count <= start)
        {
            return null;
        }

        for (var index = start; index < segments.Count; index++)
        {
            if (segments[index].Contains('['))
            {
                return null;
            }
        }

        return string.Join('/', segments.Skip(start));
    }

    private static bool IsIdentifier(string name) =>
        name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    // An expression or binding entry: the level of the subject it is on, as path segments, and the paths it reads.
    private sealed record ComputedEntry(string[] Level, string[] ReadPaths);
}
