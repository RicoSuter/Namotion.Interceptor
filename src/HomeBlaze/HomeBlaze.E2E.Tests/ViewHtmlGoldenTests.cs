using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// Pins the HTML that View mode renders for a page of text, which the text editing of Inline mode must not change.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public partial class ViewHtmlGoldenTests
{
    private const int Timeout = 30000;
    private const string PageName = "TextBlocks.md";
    private const string GoldenFileName = "TextBlocks.view.html";

    private readonly PlaywrightFixture _fixture;

    public ViewHtmlGoldenTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAPageOfTextIsViewed_ThenItsHtmlIsTheGoldenHtml()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);
        var content = page.Locator(".markdown-content");
        await Assertions.Expect(content.GetByText("Closing paragraph.")).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var html = BlazorMarker().Replace(await content.InnerHTMLAsync(), string.Empty);

        // Assert
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFileName);
        if (!File.Exists(goldenPath))
        {
            var receivedPath = Path.Combine(AppContext.BaseDirectory, "Golden", "TextBlocks.view.received.html");
            Directory.CreateDirectory(Path.GetDirectoryName(receivedPath)!);
            await File.WriteAllTextAsync(receivedPath, html);
            Assert.Fail($"There is no golden file yet. Review {receivedPath} and copy it to src/HomeBlaze/HomeBlaze.E2E.Tests/Golden/{GoldenFileName}.");
        }

        Assert.Equal(await File.ReadAllTextAsync(goldenPath), html);
    }

    // The comments that Blazor keeps in the document as boundaries of components.
    [GeneratedRegex("<!--!-->")]
    private static partial Regex BlazorMarker();
}
