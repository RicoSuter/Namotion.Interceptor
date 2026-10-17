using System.Text.RegularExpressions;
using HomeBlaze.Components;
using HomeBlaze.Storage.Pages;
using MudBlazor;

namespace HomeBlaze.Storage.Tests.Pages;

public partial class RepositoryPagesTests
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
        Assert.Contains("The subject(name) fence is no longer supported.", html);
    }

    // An unknown icon name falls back to the article icon without any message, so a misspelled icon is only seen here.
    [Theory]
    [MemberData(nameof(PagePaths))]
    public void WhenRepositoryPageIsRead_ThenEveryIconArgumentNamesAMudBlazorIcon(string relativePath)
    {
        // Arrange
        var text = ReadPage(relativePath);

        // Act
        var unknownIconNames = IconArgument().Matches(text)
            .Select(match => match.Groups["name"].Value)
            .Where(name => name != "Article" && MudBlazorIconResolver.Resolve(name) == Icons.Material.Filled.Article)
            .ToList();

        // Assert
        Assert.Empty(unknownIconNames);
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

    // Also in a code fence and in the text, where a sample shows an argument that a reader copies.
    [GeneratedRegex("""(?<![\w])Icon=(?:"(?<name>[^"]*)"|(?<name>\w+))""")]
    private static partial Regex IconArgument();
}
