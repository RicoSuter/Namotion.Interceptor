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

    internal const string DemoFolder = "src/HomeBlaze/HomeBlaze/Data/Files/Demo/";

    // Left out of the guard: this page shows every block error on purpose, so that a reader sees the red boxes.
    private const string ErrorsDemoPage = DemoFolder + "Errors.md";

    public static TheoryData<string> PagePaths()
    {
        var root = FindRepositoryRoot();
        var data = new TheoryData<string>();
        foreach (var folder in PageFolders)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.md", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relativePath != ErrorsDemoPage)
                {
                    data.Add(relativePath);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PagePaths))]
    public void WhenRepositoryPageIsParsed_ThenItHasNoBlockSyntaxErrorAndNoOldFence(string relativePath)
    {
        // Arrange
        var page = MarkdownPageParser.Parse(ReadPage(relativePath));

        // Act
        var html = PageErrorBoxes.RenderSyntaxOnly(page);

        // Assert
        Assert.DoesNotContain(PageErrorBoxes.CssClass, html);
    }

    [Fact]
    public void WhenErrorsDemoPageIsParsed_ThenItHasBlockSyntaxErrorsAndAnOldFence()
    {
        // Arrange
        var page = MarkdownPageParser.Parse(ReadPage(ErrorsDemoPage));

        // Act
        var html = PageErrorBoxes.RenderSyntaxOnly(page);

        // Assert
        Assert.Contains("closes no block.", html);
        Assert.Contains("This block is not closed.", html);
        Assert.Contains("A block inside another block needs more", html);
        Assert.Contains("The subject(name) fence is no longer supported.", html);
    }

    internal static string ReadPage(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath)).Replace("\r\n", "\n");

    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Namotion.Interceptor.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
