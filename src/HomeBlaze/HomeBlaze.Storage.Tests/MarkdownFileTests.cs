using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Storage.Files;
using Moq;
using Xunit;

namespace HomeBlaze.Storage.Tests;

public class MarkdownFileTests
{
    private static MarkdownFile CreateMarkdownFile(string fileName)
    {
        var mockStorage = new Mock<IStorageContainer>();
        return new MarkdownFile(mockStorage.Object, fileName);
    }

    [Fact]
    public void ExtractTitleFromContent_WithNavigationTitle_ReturnsNavigationTitle()
    {
        var content = """
            ---
            navigation_title: My Nav Title
            title: Regular Title
            ---
            # Content
            """;

        var result = MarkdownFile.ExtractTitleFromContent(content);

        Assert.Equal("My Nav Title", result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithNavTitle_ReturnsNavTitle()
    {
        var content = """
            ---
            nav_title: My Nav Title
            title: Regular Title
            ---
            # Content
            """;

        var result = MarkdownFile.ExtractTitleFromContent(content);

        Assert.Equal("My Nav Title", result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithOnlyTitle_ReturnsTitle()
    {
        var content = """
            ---
            title: Regular Title
            ---
            # Content
            """;

        var result = MarkdownFile.ExtractTitleFromContent(content);

        Assert.Equal("Regular Title", result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithQuotedTitle_RemovesQuotes()
    {
        var content = """
            ---
            title: "Quoted Title"
            ---
            # Content
            """;

        var result = MarkdownFile.ExtractTitleFromContent(content);

        Assert.Equal("Quoted Title", result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithSingleQuotedTitle_RemovesQuotes()
    {
        var content = """
            ---
            title: 'Single Quoted'
            ---
            # Content
            """;

        var result = MarkdownFile.ExtractTitleFromContent(content);

        Assert.Equal("Single Quoted", result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithoutFrontMatter_ReturnsNull()
    {
        var content = "# Just Content\nNo front matter here";

        var result = MarkdownFile.ExtractTitleFromContent(content);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithEmptyFrontMatter_ReturnsNull()
    {
        var content = """
            ---
            ---
            # Content
            """;

        var result = MarkdownFile.ExtractTitleFromContent(content);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithEmptyContent_ReturnsNull()
    {
        var result = MarkdownFile.ExtractTitleFromContent("");

        Assert.Null(result);
    }

    [Fact]
    public void ExtractTitleFromContent_WithNavigationTitlePriority_ReturnsNavigationTitle()
    {
        var content = """
            ---
            title: Regular Title
            nav_title: Nav Title
            navigation_title: Navigation Title
            ---
            # Content
            """;

        var result = MarkdownFile.ExtractTitleFromContent(content);

        // navigation_title has highest priority
        Assert.Equal("Navigation Title", result);
    }

    [Fact]
    public void Title_WithCachedTitle_ReturnsCachedTitle()
    {
        var markdown = CreateMarkdownFile("test.md");
        markdown.SetTitle("Cached Title");

        Assert.Equal("Cached Title", markdown.Title);
    }

    [Fact]
    public void Title_WithoutCachedTitle_ReturnsFileNameWithoutExtension()
    {
        var markdown = CreateMarkdownFile("my-document.md");

        Assert.Equal("my-document", markdown.Title);
    }

    [Fact]
    public void SetTitle_UpdatesTitle()
    {
        var markdown = CreateMarkdownFile("test.md");

        markdown.SetTitle("New Title");

        Assert.Equal("New Title", markdown.Title);
    }

    [Fact]
    public void Icon_ReturnsMarkdownIcon()
    {
        var markdown = CreateMarkdownFile("test.md");

        Assert.NotNull(markdown.Icon);
        Assert.Contains("svg", markdown.Icon);
    }
}
