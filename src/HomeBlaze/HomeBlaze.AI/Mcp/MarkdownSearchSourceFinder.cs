using HomeBlaze.Abstractions;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using HomeBlaze.Services.Metadata;
using HomeBlaze.Storage.Abstractions.Metadata;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Abstractions;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Finds what search_markdown searches: the Markdown pages and the notes of the metadata of subjects and properties.
/// </summary>
internal sealed class MarkdownSearchSourceFinder
{
    private readonly McpServerConfiguration _configuration;

    /// <param name="configuration">
    /// The configuration of the tools: its path provider and path prefix build the paths of the pages, its access policy
    /// decides which pages and notes are found, and the subjects of its excluded types, and the subjects below them, are
    /// left out.
    /// </param>
    public MarkdownSearchSourceFinder(McpServerConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Gets the Markdown files and the notes of metadata in or under <paramref name="pathPrefix"/>, all when it is null,
    /// in order of path, then property and then a page before notes, so that results and truncation are stable; without those of a subject of an
    /// excluded type or below one, which browse and search do not list. A page is found only when it is readable for the
    /// tools at its path and the policy allows to read and execute its ReadRawContent, and notes only when
    /// get_property can read them at the path of their metadata.
    /// </summary>
    public List<MarkdownSearchSource> Find(ISubjectRegistry registry, IInterceptorSubject rootSubject, string? pathPrefix)
    {
        var sources = new List<MarkdownSearchSource>();
        if (rootSubject.TryGetRegisteredSubject() is not { } root)
        {
            return sources;
        }

        foreach (var (_, registered) in registry.KnownSubjects)
        {
            var file = registered.Subject as MarkdownFile;
            var notes = TryGetNotes(registered);

            // Most subjects are neither, and the path and the exclusion walk up the parents.
            if ((file is null && notes is null) ||
                TryGetPath(registered, root) is not { } path ||
                !IsInOrUnder(path, pathPrefix))
            {
                continue;
            }

            AddReadableSources(sources, root, registered, path, file, notes);
        }

        sources.Sort(CompareSources);
        return sources;
    }

    private void AddReadableSources(
        List<MarkdownSearchSource> sources, RegisteredSubject root, RegisteredSubject subject, string path,
        MarkdownFile? file, List<(string? PropertyName, string? Title, string Notes)>? notes)
    {
        if (file is not null && IsPageReadable(root, subject, path))
        {
            sources.Add(new MarkdownSearchSource(path, null, file.Title, file, null));
        }

        if (notes is null)
        {
            return;
        }

        foreach (var (propertyName, title, text) in notes)
        {
            if (AgentMetadataAccess.IsMetadataReadable(_configuration, root, subject, path, propertyName))
            {
                sources.Add(new MarkdownSearchSource(path, propertyName, title, null, text));
            }
        }
    }

    // As invoke_query would call ReadRawContent: the page is readable at its path, and its method readable and executable.
    private bool IsPageReadable(RegisteredSubject root, RegisteredSubject page, string path)
    {
        var readable = path == _configuration.PathPrefix ? root : _configuration.TryGetReadableSubject(root, path);
        return ReferenceEquals(readable, page) &&
            page.TryGetProperty(nameof(MarkdownFile.ReadRawContent)) is { } method &&
            _configuration.IsAllowed(method, McpAccessActions.Read) &&
            _configuration.IsAllowed(method, AuthorizationActions.Execute.Name);
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
            else if (subject.TryGetProperty(propertyName) is { } property && _configuration.PathProvider.IsPropertyIncluded(property))
            {
                (notes ??= []).Add((propertyName, property.GetDisplayName(), text));
            }
        }

        return notes;
    }

    // The path as search lists it, null for a subject search does not list and also for one below a subject of an
    // excluded type, which search lists but browse does not list in the tree.
    private string? TryGetPath(RegisteredSubject subject, RegisteredSubject root)
    {
        if (ReferenceEquals(subject.Subject, root.Subject))
        {
            return _configuration.PathPrefix;
        }

        return subject.Parents.Length == 0 || AgentMetadataAccess.IsOfExcludedType(subject, root.Subject, _configuration.ExcludeTypes)
            ? null
            : _configuration.TryGetSubjectPath(root, subject);
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
