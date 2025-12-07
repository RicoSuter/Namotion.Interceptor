using HomeBlaze.Abstractions.Storage;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
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
    public void FrontmatterParser_WithNavigationTitle_ReturnsNavigationTitle()
    {
        var content = """
            ---
            navTitle: My Nav Title
            title: Regular Title
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("My Nav Title", result.GetNavTitle());
    }

    [Fact]
    public void FrontmatterParser_WithNavTitle_ReturnsNavTitle()
    {
        var content = """
            ---
            nav_title: My Nav Title
            title: Regular Title
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("My Nav Title", result.GetNavTitle());
    }

    [Fact]
    public void FrontmatterParser_WithOnlyTitle_ReturnsTitle()
    {
        var content = """
            ---
            title: Regular Title
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("Regular Title", result.Title);
    }

    [Fact]
    public void FrontmatterParser_WithQuotedTitle_RemovesQuotes()
    {
        var content = """
            ---
            title: "Quoted Title"
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("Quoted Title", result.Title);
    }

    [Fact]
    public void FrontmatterParser_WithOrder_ReturnsOrder()
    {
        var content = """
            ---
            title: Test
            order: 5
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal(5, result.Order);
    }

    [Fact]
    public void FrontmatterParser_WithIcon_ReturnsIcon()
    {
        var content = """
            ---
            title: Test
            icon: mdi-home
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.NotNull(result);
        Assert.Equal("mdi-home", result.Icon);
    }

    [Fact]
    public void FrontmatterParser_WithoutFrontMatter_ReturnsNull()
    {
        var content = "# Just Content\nNo front matter here";

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.Null(result);
    }

    [Fact]
    public void FrontmatterParser_WithEmptyFrontMatter_ReturnsNull()
    {
        var content = """
            ---
            ---
            # Content
            """;

        var result = FrontmatterParser.Parse<MarkdownFrontmatter>(content);

        Assert.Null(result);
    }

    [Fact]
    public void FrontmatterParser_WithEmptyContent_ReturnsNull()
    {
        var result = FrontmatterParser.Parse<MarkdownFrontmatter>("");

        Assert.Null(result);
    }

    [Fact]
    public void GetContentAfterFrontmatter_RemovesFrontmatter()
    {
        var content = """
            ---
            title: Test
            ---
            # Content Here
            """;

        var result = FrontmatterParser.GetContentAfterFrontmatter(content);

        Assert.Equal("# Content Here", result);
    }

    [Fact]
    public void GetContentAfterFrontmatter_WithoutFrontmatter_ReturnsOriginal()
    {
        var content = "# Content Here";

        var result = FrontmatterParser.GetContentAfterFrontmatter(content);

        Assert.Equal(content, result);
    }

    [Fact]
    public void MarkdownFile_Title_WithFrontmatter_ReturnsFrontmatterTitle()
    {
        var markdown = CreateMarkdownFile("test.md");
        var content = """
            ---
            title: Frontmatter Title
            ---
            # Content
            """;

        markdown.SetContent(content);

        Assert.Equal("Frontmatter Title", markdown.Title);
    }

    [Fact]
    public void MarkdownFile_Title_WithoutFrontmatter_ReturnsFormattedFilename()
    {
        var markdown = CreateMarkdownFile("my-test-document.md");

        Assert.Equal("My Test Document", markdown.Title);
    }

    [Fact]
    public void MarkdownFile_NavigationTitle_WithFrontmatter_ReturnsNavTitle()
    {
        var markdown = CreateMarkdownFile("test.md");
        var content = """
            ---
            title: Regular Title
            navTitle: Nav Title
            ---
            # Content
            """;

        markdown.SetContent(content);

        Assert.Equal("Nav Title", markdown.NavigationTitle);
    }

    [Fact]
    public void MarkdownFile_NavigationOrder_WithFrontmatter_ReturnsOrder()
    {
        var markdown = CreateMarkdownFile("test.md");
        var content = """
            ---
            title: Test
            order: 3
            ---
            # Content
            """;

        markdown.SetContent(content);

        Assert.Equal(3, markdown.NavigationOrder);
    }

    [Fact]
    public void MarkdownFile_Icon_WithFrontmatter_ReturnsCustomIcon()
    {
        var markdown = CreateMarkdownFile("test.md");
        var content = """
            ---
            title: Test
            icon: mdi-custom
            ---
            # Content
            """;

        markdown.SetContent(content);

        Assert.Equal("mdi-custom", markdown.Icon);
    }

    [Fact]
    public void MarkdownFile_Icon_WithoutFrontmatter_ReturnsDefaultIcon()
    {
        var markdown = CreateMarkdownFile("test.md");

        Assert.NotNull(markdown.Icon);
        Assert.Contains("path", markdown.Icon); // MudBlazor icons contain SVG path data
    }

    [Fact]
    public void MarkdownFile_SetContent_ClearsCachedFrontmatter()
    {
        var markdown = CreateMarkdownFile("test.md");

        var content1 = """
            ---
            title: First Title
            ---
            # Content
            """;
        markdown.SetContent(content1);
        Assert.Equal("First Title", markdown.Title);

        var content2 = """
            ---
            title: Second Title
            ---
            # Content
            """;
        markdown.SetContent(content2);
        Assert.Equal("Second Title", markdown.Title);
    }
}
