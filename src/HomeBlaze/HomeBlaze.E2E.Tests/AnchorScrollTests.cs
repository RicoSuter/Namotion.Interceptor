using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for links to a heading and to a block of the same page, which scroll their target to just below the top bar.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class AnchorScrollTests
{
    private const int Timeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public AnchorScrollTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("To the heading", "target-heading")]
    [InlineData("To the block", "targetblock")]
    public async Task WhenALinkToAnAnchorOfThePageIsClicked_ThenItsTargetStandsBelowTheTopBar(string linkText, string targetId)
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 700);
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Anchors.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);

        // Act
        await page.GetByRole(AriaRole.Link, new() { Name = linkText, Exact = true }).ClickAsync();

        // Assert: the page has scrolled, and the target is in the window and not under the top bar.
        await page.WaitForFunctionAsync(
            """
            id => {
                const target = document.getElementById(id);
                const bar = document.querySelector('.mud-appbar');
                if (!target || !bar || window.scrollY === 0) return false;
                const top = target.getBoundingClientRect().top;
                return top >= bar.getBoundingClientRect().bottom && top < window.innerHeight / 2;
            }
            """,
            targetId,
            new() { Timeout = Timeout });
    }
}
