using System.Text;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Moq;

namespace HomeBlaze.Storage.Tests;

public class MarkdownFileTests
{
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
        Assert.Equal("My Nav Title", result.NavTitle);
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
        Assert.Equal("My Nav Title", result.NavTitle);
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
    public async Task MarkdownFile_Title_WithFrontmatter_ReturnsFrontmatterTitle()
    {
        var storage = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Frontmatter Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = new MarkdownFile(storage, "test.md");
        await markdown.RefreshAsync(CancellationToken.None);

        Assert.Equal("Frontmatter Title", markdown.Title);
    }

    [Fact]
    public async Task MarkdownFile_Title_WithoutFrontmatter_ReturnsFormattedFilename()
    {
        var storage = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "my-test-document.md", "# Content");

        var markdown = new MarkdownFile(storage, "my-test-document.md");
        await markdown.RefreshAsync(CancellationToken.None);

        Assert.Equal("My Test Document", markdown.Title);
    }

    [Fact]
    public async Task MarkdownFile_NavigationTitle_WithFrontmatter_ReturnsNavTitle()
    {
        var storage = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Regular Title
            navTitle: Nav Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = new MarkdownFile(storage, "test.md");
        await markdown.RefreshAsync(CancellationToken.None);

        Assert.Equal("Nav Title", markdown.NavigationTitle);
    }

    [Fact]
    public async Task MarkdownFile_NavigationOrder_WithFrontmatter_ReturnsOrder()
    {
        var storage = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Test
            order: 3
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = new MarkdownFile(storage, "test.md");
        await markdown.RefreshAsync(CancellationToken.None);

        Assert.Equal(3, markdown.NavigationOrder);
    }

    [Fact]
    public async Task MarkdownFile_Icon_WithFrontmatter_ReturnsCustomIcon()
    {
        var storage = await CreateInMemoryStorageAsync();
        var content = """
            ---
            title: Test
            icon: mdi-custom
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content);

        var markdown = new MarkdownFile(storage, "test.md");
        await markdown.RefreshAsync(CancellationToken.None);

        Assert.Equal("mdi-custom", markdown.Icon);
    }

    [Fact]
    public async Task MarkdownFile_Icon_WithoutFrontmatter_ReturnsDefaultIcon()
    {
        var storage = await CreateInMemoryStorageAsync();
        await WriteFileAsync(storage, "test.md", "# Content");

        var markdown = new MarkdownFile(storage, "test.md");
        await markdown.RefreshAsync(CancellationToken.None);

        Assert.Equal("Article", markdown.Icon);
    }

    [Fact]
    public async Task MarkdownFile_Refresh_UpdatesFrontmatter()
    {
        var storage = await CreateInMemoryStorageAsync();
        var content1 = """
            ---
            title: First Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content1);

        var markdown = new MarkdownFile(storage, "test.md");
        await markdown.RefreshAsync(CancellationToken.None);
        Assert.Equal("First Title", markdown.Title);

        var content2 = """
            ---
            title: Second Title
            ---
            # Content
            """;
        await WriteFileAsync(storage, "test.md", content2);
        await markdown.RefreshAsync(CancellationToken.None);

        Assert.Equal("Second Title", markdown.Title);
    }

    private static async Task<FluentStorageContainer> CreateInMemoryStorageAsync()
    {
        var typeProvider = new TypeProvider();
        var typeRegistry = new SubjectTypeRegistry(typeProvider);
        var mockServiceProvider = new Mock<IServiceProvider>();
        var serializer = new ConfigurableSubjectSerializer(typeRegistry, mockServiceProvider.Object);
        var storage = new FluentStorageContainer(typeRegistry, serializer);
        storage.StorageType = "inmemory";
        await storage.ConnectAsync(CancellationToken.None);
        return storage;
    }

    private static async Task WriteFileAsync(FluentStorageContainer storage, string path, string content)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await storage.WriteBlobAsync(path, stream, CancellationToken.None);
    }
}
