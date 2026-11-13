using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the size of pictures on markdown pages.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MarkdownImageTests
{
    private const int Timeout = 30000;
    private const double SizeTolerance = 1.0;
    private const int NarrowViewportWidth = 390;
    private const int ViewportHeight = 800;

    private readonly PlaywrightFixture _fixture;

    public MarkdownImageTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync(int viewportWidth)
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(viewportWidth, ViewportHeight);
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Images.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // A picture has its size only once it is loaded.
        await Assertions.Expect(page.GetByAltText("Picture in a card")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await page.WaitForFunctionAsync(
            "() => [...document.images].every(image => image.alt === 'Missing picture' || (image.complete && image.naturalWidth > 0))",
            null,
            new() { Timeout = Timeout });
        return page;
    }

    private static async Task<LocatorBoundingBoxResult> GetBoxAsync(ILocator locator)
    {
        return await locator.BoundingBoxAsync() ?? throw new InvalidOperationException("The element has no box.");
    }

    [Fact]
    public async Task WhenPictureIsWiderThanThePage_ThenItIsAsWideAsTheTextAndKeepsItsProportions()
    {
        // Arrange
        var page = await OpenPageAsync(viewportWidth: 1280);

        // Act
        var picture = await GetBoxAsync(page.GetByAltText("Wide picture"));
        var paragraph = await GetBoxAsync(page.Locator("p").Filter(new() { Has = page.GetByAltText("Wide picture") }));

        // Assert
        Assert.True(paragraph.Width < 1280, $"The text is {paragraph.Width} pixels wide");
        Assert.Equal(paragraph.Width, picture.Width, SizeTolerance);
        Assert.Equal(picture.Width / 4, picture.Height, SizeTolerance);
    }

    [Fact]
    public async Task WhenPictureIsTall_ThenItIsAtMostFourFifthsOfTheViewportHighAndKeepsItsProportions()
    {
        // Arrange
        var page = await OpenPageAsync(viewportWidth: 1280);

        // Act
        var picture = await GetBoxAsync(page.GetByAltText("Tall picture"));

        // Assert
        Assert.Equal(ViewportHeight * 0.8, picture.Height, SizeTolerance);
        Assert.Equal(picture.Height / 4, picture.Width, SizeTolerance);
    }

    [Fact]
    public async Task WhenPictureCannotBeLoaded_ThenAPlaceholderWithItsTextStandsInItsPlace()
    {
        // Arrange & Act
        var page = await OpenPageAsync(viewportWidth: 1280);
        var picture = page.GetByAltText("Missing picture");

        // Assert: the placeholder is a picture of its own, so it has a size, and it names the picture that is missing.
        await Assertions.Expect(picture).ToHaveClassAsync(new Regex("markdown-picture-missing"), new() { Timeout = Timeout });
        await Assertions.Expect(picture).ToHaveAttributeAsync("title", new Regex(@"/missing-picture\.png$"));
        var source = await picture.GetAttributeAsync("src") ?? "";
        Assert.StartsWith("data:image/svg+xml,", source);
        Assert.Contains("Picture not available", Uri.UnescapeDataString(source));
        Assert.Contains("Missing picture", Uri.UnescapeDataString(source));
        var box = await GetBoxAsync(picture);
        Assert.Equal(320, box.Width, SizeTolerance);
        Assert.Equal(96, box.Height, SizeTolerance);
    }

    [Fact]
    public async Task WhenPictureCanBeLoaded_ThenItIsNotReplacedByAPlaceholder()
    {
        // Arrange & Act
        var page = await OpenPageAsync(viewportWidth: 1280);
        await Assertions.Expect(page.GetByAltText("Missing picture")).ToHaveClassAsync(new Regex("markdown-picture-missing"), new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(page.Locator(".markdown-picture-missing")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByAltText("Wide picture")).ToHaveAttributeAsync("src", new Regex("^data:image/svg\\+xml;base64,"));
    }

    [Fact]
    public async Task WhenPictureHasAWidthOfItsOwn_ThenItKeepsThatWidth()
    {
        // Arrange
        var page = await OpenPageAsync(viewportWidth: 1280);

        // Act
        var picture = await GetBoxAsync(page.GetByAltText("Sized picture"));

        // Assert
        Assert.Equal(700, picture.Width, SizeTolerance);
        Assert.Equal(175, picture.Height, SizeTolerance);
    }

    [Fact]
    public async Task WhenPictureStandsInANarrowCard_ThenItIsAsWideAsTheContentOfTheCard()
    {
        // Arrange
        var page = await OpenPageAsync(viewportWidth: 1280);
        var card = page.GetByTestId("card").Filter(new() { HasText = "Picture Card" });

        // Act
        var picture = await GetBoxAsync(card.GetByAltText("Picture in a card"));
        var paragraph = await GetBoxAsync(card.Locator("p").Filter(new() { Has = page.GetByAltText("Picture in a card") }));

        // Assert
        Assert.True(paragraph.Width < 400, $"The text of the card is {paragraph.Width} pixels wide");
        Assert.Equal(paragraph.Width, picture.Width, SizeTolerance);
        Assert.Equal(picture.Width / 4, picture.Height, SizeTolerance);
    }

    [Fact]
    public async Task WhenViewportIsNarrow_ThenEveryPictureFitsWithItsProportionsAndThePageDoesNotScrollSideways()
    {
        // Arrange
        var page = await OpenPageAsync(viewportWidth: NarrowViewportWidth);

        // Act
        var pictures = new[]
        {
            await GetBoxAsync(page.GetByAltText("Wide picture")),
            await GetBoxAsync(page.GetByAltText("Sized picture"))
        };
        var scrollsSideways = await page.EvaluateAsync<bool>(
            """
            () => [document.scrollingElement, ...document.querySelectorAll('.mud-main-content, .mud-layout')]
                .some(element => element.scrollWidth > element.clientWidth)
            """);

        // Assert
        Assert.All(pictures, picture =>
        {
            Assert.InRange(picture.X + picture.Width, 0, NarrowViewportWidth);
            Assert.True(picture.Width > 200, $"The picture is {picture.Width} pixels wide");
            Assert.Equal(picture.Width / 4, picture.Height, SizeTolerance);
        });
        Assert.False(scrollsSideways);
    }
}
