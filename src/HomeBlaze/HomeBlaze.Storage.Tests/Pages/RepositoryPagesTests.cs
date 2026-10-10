using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

public class RepositoryPagesTests
{
    private static readonly string[] PageFolders =
    [
        "src/HomeBlaze/HomeBlaze/Data/Files",
        "src/HomeBlaze/HomeBlaze/Seed/Files",
        "src/HomeBlaze/HomeBlaze.E2E.Tests/TestData"
    ];

    public static TheoryData<string> PagePaths()
    {
        var root = FindRepositoryRoot();
        var data = new TheoryData<string>();
        foreach (var folder in PageFolders)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.md", SearchOption.AllDirectories))
            {
                data.Add(Path.GetRelativePath(root, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PagePaths))]
    public void WhenRepositoryPageIsParsed_ThenItHasNoBlockSyntaxErrorAndNoOldFence(string relativePath)
    {
        // Arrange
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath)).Replace("\r\n", "\n");
        var page = MarkdownPageParser.Parse(source);

        // Act
        var html = Flatten(MarkdownSegmentBuilder.Build(page));

        // Assert
        Assert.DoesNotContain("markdown-block-error", html);
    }

    private static string Flatten(IEnumerable<MarkdownSegment> segments) =>
        string.Concat(segments.Select(segment => segment switch
        {
            HtmlMarkdownSegment html => html.Html,
            BlockMarkdownSegment block => Flatten(block.Content),
            _ => string.Empty
        }));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Namotion.Interceptor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
