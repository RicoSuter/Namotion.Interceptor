using HomeBlaze.Abstractions;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Finds what search_markdown searches: the Markdown pages and the notes of the metadata of subjects and properties.
/// </summary>
internal sealed class MarkdownSearchSourceFinder
{
    private readonly PathProviderBase _pathProvider;
    private readonly string _pathPrefix;
    private readonly Type[] _excludeTypes;

    /// <param name="pathProvider">Builds the paths of the pages, as the other tools resolve them.</param>
    /// <param name="pathPrefix">The prefix of every path.</param>
    /// <param name="excludeTypes">The types whose subjects, and the subjects below them, are left out.</param>
    public MarkdownSearchSourceFinder(PathProviderBase pathProvider, string pathPrefix, Type[] excludeTypes)
    {
        _pathProvider = pathProvider;
        _pathPrefix = pathPrefix;
        _excludeTypes = excludeTypes;
    }

    /// <summary>
    /// Gets the Markdown files and the notes of metadata in or under <paramref name="pathPrefix"/>, all when it is null,
    /// in order of path and then property so that results and truncation are stable; without those of a subject of an excluded type or below
    /// one, which browse and search do not show.
    /// </summary>
    public List<MarkdownSearchSource> Find(ISubjectRegistry registry, IInterceptorSubject rootSubject, string? pathPrefix)
    {
        var sources = new List<MarkdownSearchSource>();
        foreach (var (_, registered) in registry.KnownSubjects)
        {
            var subject = registered.Subject;
            var path = ReferenceEquals(subject, rootSubject) ? _pathPrefix : TryGetPath(registered, rootSubject);
            if (path is null || AgentMetadataAccess.IsOfExcludedType(registered, rootSubject, _excludeTypes))
            {
                continue;
            }

            if (subject is MarkdownFile file)
            {
                Add(new MarkdownSearchSource(path, null, file.Title, file, null));
            }

            foreach (var (propertyName, title, notes) in GetNotes(registered))
            {
                Add(new MarkdownSearchSource(path, propertyName, title, null, notes));
            }
        }

        sources.Sort(static (left, right) =>
        {
            var order = string.CompareOrdinal(left.Path, right.Path);
            return order != 0 ? order : string.CompareOrdinal(left.Property, right.Property);
        });
        return sources;

        void Add(MarkdownSearchSource source)
        {
            if (pathPrefix is null || IsInOrUnder(source.Path, pathPrefix))
            {
                sources.Add(source);
            }
        }
    }

    // The notes of the subject, with its title, and those of each of its properties that the path provider includes,
    // with the name and the display name of the property.
    private IEnumerable<(string? PropertyName, string? Title, string Notes)> GetNotes(RegisteredSubject subject)
    {
        foreach (var (propertyName, metadata) in MetadataSearch.GetEntries(subject))
        {
            if (metadata.GetString(MetadataKeys.Notes) is not { } notes)
            {
                continue;
            }

            if (propertyName is null)
            {
                yield return (null, (subject.Subject as ITitleProvider)?.Title, notes);
            }
            else if (subject.TryGetProperty(propertyName) is { } property && _pathProvider.IsPropertyIncluded(property))
            {
                yield return (propertyName, property.GetDisplayName(), notes);
            }
        }
    }

    // The path as browse and search show it: the path through the first parent, after the path prefix.
    private string? TryGetPath(RegisteredSubject subject, IInterceptorSubject rootSubject)
    {
        var parents = subject.Parents;
        if (parents.Length == 0)
        {
            return null;
        }

        var parent = parents[0];
        var path = parent.Property.TryGetPath(_pathProvider, rootSubject, parent.Index);
        return path is null ? null : _pathPrefix + path;
    }

    // Whole segments only, so /Docs does not match /DocsArchive/Page.md.
    private static bool IsInOrUnder(string path, string pathPrefix) =>
        path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) &&
        (path.Length == pathPrefix.Length || path[pathPrefix.Length] == '/');
}

/// <summary>
/// A page, or the notes of the metadata of a subject or of its property, that search_markdown searches: the path of the
/// page or subject, and the name of the property or null.
/// </summary>
internal readonly record struct MarkdownSearchSource(string Path, string? Property, string? Title, MarkdownFile? File, string? Notes)
{
    /// <summary>Gets the view of a page with secret values hidden, null while the page was not parsed yet; or the notes.</summary>
    public string? ReadContent() => File is not null ? File.ReadRawContent() : Notes;
}
