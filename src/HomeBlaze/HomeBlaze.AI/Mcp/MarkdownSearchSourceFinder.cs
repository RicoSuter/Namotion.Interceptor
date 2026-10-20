using HomeBlaze.Abstractions;
using HomeBlaze.AI.Agents;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Finds what search_markdown searches: the Markdown pages and the notes of the metadata of subjects and properties.
/// </summary>
internal sealed class MarkdownSearchSourceFinder
{
    private readonly PathProviderBase _pathProvider;
    private readonly string _pathPrefix;
    private readonly Type[] _excludeTypes;
    private readonly bool _forAgents;

    /// <param name="pathProvider">Builds the paths of the pages, as the other tools resolve them.</param>
    /// <param name="pathPrefix">The prefix of every path.</param>
    /// <param name="excludeTypes">The types whose subjects, and the subjects below them, are left out.</param>
    /// <param name="forAgents">Whether the notes of an agent and of a subject that an agent holds through any parent are left out.</param>
    public MarkdownSearchSourceFinder(PathProviderBase pathProvider, string pathPrefix, Type[] excludeTypes, bool forAgents)
    {
        _pathProvider = pathProvider;
        _pathPrefix = pathPrefix;
        _excludeTypes = excludeTypes;
        _forAgents = forAgents;
    }

    /// <summary>
    /// Gets the Markdown files and the notes of metadata in or under <paramref name="pathPrefix"/>, all when it is null,
    /// in order of path, then property and then a page before notes, so that results and truncation are stable; without those of a subject of an
    /// excluded type or below one, which browse and search do not show, and for agents without the notes of agents.
    /// </summary>
    public List<MarkdownSearchSource> Find(ISubjectRegistry registry, IInterceptorSubject rootSubject, string? pathPrefix)
    {
        var sources = new List<MarkdownSearchSource>();
        foreach (var (_, registered) in registry.KnownSubjects)
        {
            var file = registered.Subject as MarkdownFile;

            // Agents neither change nor find the metadata of agents, see AgentMetadataAccess; the parents are walked only
            // for notes, as most subjects have none.
            var notes = TryGetNotes(registered);
            if (notes is not null && _forAgents && registered.Subject.TryGetFirstParentOrSelf<LlmAgentBase>() is not null)
            {
                notes = null;
            }

            // Most subjects are neither, and the path and the exclusion walk up the parents.
            if ((file is null && notes is null) ||
                TryGetPath(registered, rootSubject) is not { } path ||
                !IsInOrUnder(path, pathPrefix))
            {
                continue;
            }

            if (file is not null)
            {
                sources.Add(new MarkdownSearchSource(path, null, file.Title, file, null));
            }

            if (notes is not null)
            {
                foreach (var (propertyName, title, text) in notes)
                {
                    sources.Add(new MarkdownSearchSource(path, propertyName, title, null, text));
                }
            }
        }

        sources.Sort(CompareSources);
        return sources;
    }

    // Sort is not stable, so a page comes before the notes of its own subject by a rule of its own.
    private static int CompareSources(MarkdownSearchSource left, MarkdownSearchSource right)
    {
        var order = string.CompareOrdinal(left.Path, right.Path);
        order = order != 0 ? order : string.CompareOrdinal(left.Property, right.Property);
        return order != 0 ? order : (right.File is not null).CompareTo(left.File is not null);
    }

    // The notes of the subject, with its title, and those of each of its properties that the path provider includes,
    // with the name and the display name of the property; null without any.
    private List<(string? PropertyName, string? Title, string Notes)>? TryGetNotes(RegisteredSubject subject)
    {
        List<(string? PropertyName, string? Title, string Notes)>? notes = null;
        foreach (var (propertyName, metadata) in MetadataSearch.GetEntries(subject))
        {
            if (metadata.GetString(MetadataKeys.Notes) is not { } text)
            {
                continue;
            }

            if (propertyName is null)
            {
                (notes ??= []).Add((null, (subject.Subject as ITitleProvider)?.Title, text));
            }
            else if (subject.TryGetProperty(propertyName) is { } property && _pathProvider.IsPropertyIncluded(property))
            {
                (notes ??= []).Add((propertyName, property.GetDisplayName(), text));
            }
        }

        return notes;
    }

    // The path as browse and search show it: the path through the first parent, after the path prefix; null for a
    // subject of an excluded type or below one.
    private string? TryGetPath(RegisteredSubject subject, IInterceptorSubject rootSubject)
    {
        if (ReferenceEquals(subject.Subject, rootSubject))
        {
            return _pathPrefix;
        }

        if (subject.Parents is not [var parent, ..] || AgentMetadataAccess.IsOfExcludedType(subject, rootSubject, _excludeTypes))
        {
            return null;
        }

        var path = parent.Property.TryGetPath(_pathProvider, rootSubject, parent.Index);
        return path is null ? null : _pathPrefix + path;
    }

    // Whole segments only, so /Docs does not match /DocsArchive/Page.md; every path is under no prefix.
    private static bool IsInOrUnder(string path, string? pathPrefix) =>
        pathPrefix is null ||
        (path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase) &&
         (path.Length == pathPrefix.Length || path[pathPrefix.Length] == '/'));
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
