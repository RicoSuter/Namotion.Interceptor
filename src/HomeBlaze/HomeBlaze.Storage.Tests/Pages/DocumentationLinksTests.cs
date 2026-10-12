using System.Collections.Concurrent;
using HomeBlaze.Storage.Pages;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// Follows the links of the documentation, and of the demo pages that link into it, to other pages and to
/// sections, so that a page that is renamed or a heading that is reworded is found here and not by the
/// reader who clicks the link.
/// </summary>
/// <remarks>
/// A section is addressed by the id of its heading. The pages are parsed with the pipeline of a page, so
/// the ids are those that the application gives the headings.
/// </remarks>
public class DocumentationLinksTests
{
    private const string PagesFolder = "src/HomeBlaze/HomeBlaze/Data/Files";
    private const string PageExtension = ".md";

    // Broken before this test existed and not fixed yet: the two plan pages are not in the repository.
    // A link that is fixed must be taken off this list, which the second test checks.
    private static readonly (string Page, string Url)[] KnownBrokenLinks =
    [
        (PagesFolder + "/Docs/plans/ai-agents.md", "mcp-extensions.md"),
        (PagesFolder + "/Docs/plans/ai-agents.md", "../../../../../docs/plans/mcp-server.md")
    ];

    private static readonly ConcurrentDictionary<string, HashSet<string>> HeadingIds = new();

    public static TheoryData<string> Pages()
    {
        var root = RepositoryPagesTests.FindRepositoryRoot();
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, PagesFolder), "*" + PageExtension, SearchOption.AllDirectories).Order())
        {
            data.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void WhenPageLinksToAPageOrASection_ThenThePageAndTheHeadingExist(string relativePath)
    {
        // Arrange
        var links = FindLinks(relativePath);

        // Act
        var brokenLinks = links
            .Where(link => !KnownBrokenLinks.Contains((relativePath, link.Url)))
            .Select(link => FindProblem(relativePath, link.Url) is { } problem ? $"line {link.Line}: ({link.Url}) {problem}" : null)
            .OfType<string>();

        // Assert
        Assert.Empty(brokenLinks);
    }

    [Fact]
    public void WhenLinkIsListedAsKnownBroken_ThenThePageStillHasItAndItIsStillBroken()
    {
        // Arrange
        var linksOfPage = KnownBrokenLinks
            .Select(known => known.Page)
            .Distinct()
            .ToDictionary(page => page, FindLinks);

        // Act
        var fixedOrRemoved = KnownBrokenLinks.Where(known =>
            !linksOfPage[known.Page].Exists(link => link.Url == known.Url) ||
            FindProblem(known.Page, known.Url) is null);

        // Assert
        Assert.Empty(fixedOrRemoved);
    }

    /// <summary>Gets the links of the page that are not images, each with its 1-based line.</summary>
    private static List<(string Url, int Line)> FindLinks(string relativePath) =>
        MarkdownPageParser
            .Parse(RepositoryPagesTests.ReadPage(relativePath))
            .Document
            .Descendants<LinkInline>()
            .Where(link => link is { IsImage: false, Url: not null })
            .Select(link => (link.Url!, link.Line + 1))
            .ToList();

    /// <summary>Gets what is wrong with a link of the page, or null for a link that leads somewhere or is not checked.</summary>
    private static string? FindProblem(string relativePath, string url)
    {
        var file = Path.Combine(RepositoryPagesTests.FindRepositoryRoot(), relativePath);

        // A link with a scheme or to a route of the application does not lead to a file of the documentation.
        if (url.Contains(':') || url.StartsWith('/'))
        {
            return null;
        }

        var fragmentStart = url.IndexOf('#');
        var pagePath = fragmentStart < 0 ? url : url[..fragmentStart];
        var fragment = fragmentStart < 0 ? string.Empty : url[(fragmentStart + 1)..];

        // A link that is only a fragment leads to a section of the page it is on.
        var target = file;
        if (pagePath.Length > 0)
        {
            if (!pagePath.EndsWith(PageExtension, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, pagePath));
            if (!File.Exists(target))
            {
                return "leads to a page that does not exist";
            }
        }

        if (fragment.Length > 0 && !GetHeadingIds(target).Contains(fragment))
        {
            return "leads to a section that does not exist";
        }

        return null;
    }

    private static HashSet<string> GetHeadingIds(string file) =>
        HeadingIds.GetOrAdd(file, static path => MarkdownPageParser
            .Parse(File.ReadAllText(path))
            .Document
            .Descendants<HeadingBlock>()
            .Select(heading => heading.TryGetAttributes()?.Id)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal));
}
