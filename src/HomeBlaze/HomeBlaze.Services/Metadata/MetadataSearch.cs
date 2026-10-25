using System.Collections.Immutable;
using System.Text.Json;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.Services.Metadata;

/// <summary>
/// Searches the metadata of the subjects and properties below a subject. Each call walks the tree and keeps nothing.
/// Thread-safe.
/// </summary>
public static class MetadataSearch
{
    private static readonly DefaultPathProvider PathProvider = new('/');

    /// <summary>
    /// Gets the subjects and properties at or below <paramref name="start"/> (<paramref name="root"/> when null) whose
    /// metadata matches every filter that is given: a tag in <c>tags</c>, a key that is set, and a value that equals a
    /// string value, or a string in an array, of the key when one is given, else of any key. Keys match exactly, and tags
    /// and values without regard to case. The paths of the matches are relative to <paramref name="root"/>, through the
    /// first parent that reaches it, as the registry writes them (<c>Pump/Channels[0]</c>); a subject that no path from
    /// <paramref name="root"/> reaches is not found. The matches are ordered by path and then property, ordinal. A subject
    /// for which <paramref name="isExcluded"/> returns true, and every subject below it, is skipped.
    /// </summary>
    public static IReadOnlyList<MetadataSearchMatch> Search(
        IInterceptorSubject root, string? tag = null, string? key = null, string? value = null, IInterceptorSubject? start = null,
        Func<IInterceptorSubject, bool>? isExcluded = null)
    {
        var matches = new List<MetadataSearchMatch>();
        foreach (var subject in Walk(start ?? root, isExcluded))
        {
            string? subjectPath = null;
            foreach (var (propertyName, metadata) in GetEntries(subject))
            {
                if (IsMatch(metadata, tag, key, value) && (subjectPath ??= TryGetPath(subject, root)) is { } matchPath)
                {
                    matches.Add(new MetadataSearchMatch(matchPath, propertyName));
                }
            }
        }

        matches.Sort(static (left, right) =>
        {
            var order = string.CompareOrdinal(left.Path, right.Path);
            return order != 0 ? order : string.CompareOrdinal(left.Property, right.Property);
        });
        return matches;
    }

    /// <summary>
    /// Gets every tag below <paramref name="root"/>, each once without regard to case, in order. A subject for which
    /// <paramref name="isExcluded"/> returns true, and every subject below it, is skipped, and so are the tags of a
    /// property for which <paramref name="isPropertyIncluded"/> returns false.
    /// </summary>
    public static IReadOnlyList<string> GetTags(
        IInterceptorSubject root, Func<IInterceptorSubject, bool>? isExcluded = null,
        Func<RegisteredSubjectProperty, bool>? isPropertyIncluded = null)
        => Walk(root, isExcluded)
            .SelectMany(subject => GetEntries(subject)
                .Where(entry => entry.PropertyName is null || isPropertyIncluded is null ||
                                (subject.TryGetProperty(entry.PropertyName) is { } property && isPropertyIncluded(property))))
            .SelectMany(static entry => entry.Metadata.GetTags())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Gets the metadata of the subject, with a null property name, and that of each of its properties that has any,
    /// with the name of the property.
    /// </summary>
    public static IEnumerable<(string? PropertyName, ImmutableDictionary<string, JsonElement> Metadata)> GetEntries(RegisteredSubject subject)
    {
        foreach (var property in subject.Properties)
        {
            if (property.IsMetadata() && property.GetValue() is ImmutableDictionary<string, JsonElement> metadata)
            {
                yield return (property.IsAttribute ? property.AttributeMetadata.PropertyName : null, metadata);
            }
        }
    }

    private static bool IsMatch(ImmutableDictionary<string, JsonElement> metadata, string? tag, string? key, string? value)
        => (tag is null || metadata.GetTags().Contains(tag, StringComparer.OrdinalIgnoreCase)) &&
           (key is null || metadata.ContainsKey(key)) &&
           (value is null || (key is null ? metadata.Keys.SelectMany(name => GetStrings(metadata, name)) : GetStrings(metadata, key))
               .Contains(value, StringComparer.OrdinalIgnoreCase));

    // The string of the key, or the strings of its array.
    private static ImmutableArray<string> GetStrings(ImmutableDictionary<string, JsonElement> metadata, string key)
        => metadata.GetString(key) is { } text ? [text] : metadata.GetStringArray(key);

    // The path through the first parent that reaches the root.
    private static string? TryGetPath(RegisteredSubject subject, IInterceptorSubject root)
    {
        if (ReferenceEquals(subject.Subject, root))
        {
            return string.Empty;
        }

        foreach (var parent in subject.Parents)
        {
            if (parent.Property.TryGetPath(PathProvider, root, parent.Index) is { } path)
            {
                return path;
            }
        }

        return null;
    }

    // The subject and every subject below it, each once, without the excluded ones and those below them.
    private static IEnumerable<RegisteredSubject> Walk(IInterceptorSubject start, Func<IInterceptorSubject, bool>? isExcluded)
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IInterceptorSubject>();
        pending.Push(start);
        while (pending.TryPop(out var subject))
        {
            if (!visited.Add(subject) || isExcluded?.Invoke(subject) == true || subject.TryGetRegisteredSubject() is not { } registered)
            {
                continue;
            }

            yield return registered;
            foreach (var property in registered.Properties)
            {
                if (!property.IsAttribute && property.CanContainSubjects)
                {
                    foreach (var child in property.Children)
                    {
                        pending.Push(child.Subject);
                    }
                }
            }
        }
    }
}
