using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Services;

/// <summary>
/// Searches the metadata of the subjects and properties below a subject. Each call walks the tree and keeps nothing.
/// Thread-safe.
/// </summary>
public sealed class MetadataSearch
{
    private readonly SubjectPathResolver _pathResolver;

    public MetadataSearch(SubjectPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// Gets the absolute paths of the subjects and properties below <paramref name="root"/>, the root included, whose
    /// metadata matches every filter that is given: a tag in <c>tags</c>, a key, a value of a string or of a string in
    /// an array (of the key when one is given), and a path at or below the given absolute path. The path of a property
    /// is the path of its subject followed by its name. Strings match without regard to case.
    /// </summary>
    public IReadOnlyList<string> Search(IInterceptorSubject root, string? tag = null, string? key = null, string? value = null, string? path = null)
    {
        var prefix = path is null ? null : "/" + path.Trim().Trim('/');
        var paths = new List<string>();
        foreach (var (entryPath, metadata) in Walk(root))
        {
            if ((tag is null || metadata.GetStringArray(MetadataKeys.Tags).Contains(tag, StringComparer.OrdinalIgnoreCase)) &&
                (key is null || metadata.ContainsKey(key)) &&
                (value is null || (key is null ? metadata.Keys.SelectMany(name => GetStrings(metadata, name)) : GetStrings(metadata, key))
                    .Contains(value, StringComparer.OrdinalIgnoreCase)) &&
                (prefix is null || prefix == "/" || entryPath == prefix || entryPath.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                paths.Add(entryPath);
            }
        }

        return paths;
    }

    /// <summary>Gets every tag below <paramref name="root"/>, each once without regard to case, in order.</summary>
    public IReadOnlyList<string> GetTags(IInterceptorSubject root)
        => Walk(root)
            .SelectMany(static entry => entry.Metadata.GetStringArray(MetadataKeys.Tags))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The string of the key, or the strings of its array.
    private static ImmutableArray<string> GetStrings(ImmutableDictionary<string, JsonElement> metadata, string key)
        => metadata.GetString(key) is { } text ? [text] : metadata.GetStringArray(key);

    // The metadata of each subject and property below the root, with its absolute path.
    private IEnumerable<(string Path, ImmutableDictionary<string, JsonElement> Metadata)> Walk(IInterceptorSubject root)
    {
        var visited = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<IInterceptorSubject>();
        pending.Push(root);
        while (pending.TryPop(out var subject))
        {
            if (!visited.Add(subject) || subject.TryGetRegisteredSubject() is not { } registered ||
                _pathResolver.GetPath(subject, PathStyle.Canonical) is not { } subjectPath)
            {
                continue;
            }

            if (subject.GetMetadata() is { } metadata)
            {
                yield return (subjectPath, metadata);
            }

            foreach (var property in registered.Properties)
            {
                if (property.IsAttribute)
                {
                    if (property.AttributeMetadata.AttributeName == KnownAttributes.Metadata &&
                        property.GetValue() is ImmutableDictionary<string, JsonElement> propertyMetadata)
                    {
                        var propertyName = property.AttributeMetadata.PropertyName;
                        yield return (subjectPath == "/" ? "/" + propertyName : $"{subjectPath}/{propertyName}", propertyMetadata);
                    }
                }
                else if (property.CanContainSubjects)
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
