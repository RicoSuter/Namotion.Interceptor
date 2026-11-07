using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for pictures and files of the storage that a markdown page shows or links to, served under /files.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MarkdownStorageFileTests
{
    private const int Timeout = 30000;
    private const string PageRoute = "pages/Demo/StorageFiles.md";
    private const string PictureRoute = "files/Demo/images/sample.png";
    private const string TextRoute = "files/Demo/images/sample.txt";

    private readonly PlaywrightFixture _fixture;

    public MarkdownStorageFileTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}{PageRoute}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await Assertions.Expect(page.GetByAltText("Storage picture")).ToBeVisibleAsync(new() { Timeout = Timeout });
        return page;
    }

    [Fact]
    public async Task WhenPageHasARelativePicture_ThenItIsLoadedFromTheStorage()
    {
        // Arrange
        var page = await OpenPageAsync();
        var picture = page.GetByAltText("Storage picture");

        // Act
        await page.WaitForFunctionAsync(
            "() => { const image = document.querySelector('img[alt=\"Storage picture\"]'); return image.complete && image.naturalWidth > 0; }",
            null,
            new() { Timeout = Timeout });
        var size = await picture.EvaluateAsync<int[]>("image => [image.naturalWidth, image.naturalHeight]");

        // Assert
        Assert.Equal("/" + PictureRoute, await picture.GetAttributeAsync("src"));
        Assert.Equal(new[] { 4, 2 }, size);
    }

    [Fact]
    public async Task WhenPictureIsRequested_ThenItsBytesAreServedWithItsTypeAndSafeHeaders()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        var expected = await File.ReadAllBytesAsync(_fixture.GetDemoPagePath(Path.Combine("images", "sample.png")));

        // Act
        var response = await page.APIRequest.GetAsync($"{_fixture.ServerAddress}{PictureRoute}");

        // Assert
        Assert.Equal(200, response.Status);
        Assert.Equal("image/png", response.Headers["content-type"]);
        Assert.Equal("no-cache", response.Headers["cache-control"]);
        Assert.Equal("nosniff", response.Headers["x-content-type-options"]);
        Assert.Equal("sandbox", response.Headers["content-security-policy"]);
        Assert.StartsWith("W/\"", response.Headers["etag"]);
        Assert.Equal(expected, await response.BodyAsync());
    }

    [Fact]
    public async Task WhenFileLinkIsClicked_ThenTheFileOpensInANewTabAndThePageStays()
    {
        // Arrange
        var page = await OpenPageAsync();
        var link = page.GetByRole(AriaRole.Link, new() { Name = "Sample text" });

        // Act
        var newTab = await page.Context.RunAndWaitForPageAsync(() => link.ClickAsync());
        await newTab.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert
        Assert.Equal("_blank", await link.GetAttributeAsync("target"));
        Assert.Equal("noopener noreferrer", await link.GetAttributeAsync("rel"));
        Assert.Equal($"{_fixture.ServerAddress}{TextRoute}", newTab.Url);
        Assert.Contains("Sample text file of the storage.", await newTab.Locator("body").InnerTextAsync());
        Assert.Equal($"{_fixture.ServerAddress}{PageRoute}", page.Url);
    }

    [Fact]
    public async Task WhenPageHasAnExternalLink_ThenItOpensInANewTab()
    {
        // Arrange
        var page = await OpenPageAsync();

        // Act
        var link = page.GetByRole(AriaRole.Link, new() { Name = "Example site" });

        // Assert
        Assert.Equal("_blank", await link.GetAttributeAsync("target"));
        Assert.Equal("noopener noreferrer", await link.GetAttributeAsync("rel"));
    }

    [Theory]
    [InlineData("files/Demo/StorageFiles.md")]
    [InlineData("files/History")]
    [InlineData("files/Demo")]
    [InlineData("files/Demo/images/missing.png")]
    public async Task WhenPathIsNoBinaryFile_ThenItIsNotFound(string route)
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        var response = await page.APIRequest.GetAsync($"{_fixture.ServerAddress}{route}");

        // Assert
        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task WhenFileIsReplaced_ThenTheNextRequestGetsTheNewContentAndAnotherETag()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        var name = $"storage-file-{Guid.NewGuid():N}.txt";
        var path = _fixture.GetDemoPagePath(name);
        var url = $"{_fixture.ServerAddress}files/Demo/{name}";
        try
        {
            await File.WriteAllTextAsync(path, "first");
            await _fixture.ReconcileStorageAsync();
            var first = await page.APIRequest.GetAsync(url);
            Assert.Equal(200, first.Status);
            Assert.Equal("first", await first.TextAsync());

            // Another size, so that the validator changes even where the time of the file system is coarse.
            await File.WriteAllTextAsync(path, "second and longer");

            // Act
            await _fixture.ReconcileStorageAsync();
            var second = await page.APIRequest.GetAsync(url);

            // Assert
            Assert.Equal(200, second.Status);
            Assert.Equal("second and longer", await second.TextAsync());
            Assert.NotEqual(first.Headers["etag"], second.Headers["etag"]);
        }
        finally
        {
            File.Delete(path);
            await _fixture.ReconcileStorageAsync();
        }
    }
}
