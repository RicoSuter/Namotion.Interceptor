using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the tile kind of the switch and the operation button, and for the layouts that size tiles.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class TileWidgetTests
{
    private const int Timeout = 30000;
    private const string PageName = "Tiles.md";

    private readonly PlaywrightFixture _fixture;

    public TileWidgetTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private Motor GetMotor() =>
        (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["tilemotor"];

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    private static ILocator GetSwitchTile(IPage page, string label) =>
        page.GetByRole(AriaRole.Switch, new() { Name = label, Exact = true });

    private static Task<string> GetBackgroundAsync(ILocator tileButton) =>
        tileButton.Locator("xpath=..").EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("switch-tile").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenSwitchTileIsClicked_ThenItInvokesTheOperationAndTakesTheFillOfTheNewState()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Motor");
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });
        var warning = await page.ResolveColorAsync("var(--mud-palette-warning)");
        Assert.Equal(warning, await GetBackgroundAsync(tile));

        // Act
        await tile.ClickAsync();

        // Assert
        await Assertions.Expect(tile).Not.ToBeCheckedAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToBeEnabledAsync(new() { Timeout = Timeout });
        Assert.False(GetMotor().IsPowered);
        var surface = await page.ResolveColorAsync("var(--mud-palette-surface)");
        Assert.Equal(surface, await GetBackgroundAsync(tile));
        await Assertions.Expect(page.GetByTestId("switch-tile").Filter(new() { Has = tile }).GetByTestId("tile-subtitle")).ToHaveTextAsync("Hall");
    }

    [Fact]
    public async Task WhenBadgeIsClicked_ThenTheTileToggles()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Motor");
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });
        var badge = page.GetByTestId("switch-tile").Filter(new() { Has = tile }).GetByTestId("tile-badge");

        // Act: the badge takes no pointer input, so Playwright's actionability check would wait forever; a forced click
        // goes to the point, where the tile below receives it, which is what a user's click does.
        await badge.ClickAsync(new() { Force = true });

        // Assert
        await Assertions.Expect(tile).Not.ToBeCheckedAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenSwitchTileHasNoMap_ThenItIsFilledWithThePrimaryColorWhileTrue()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Plain");

        // Act
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });

        // Assert
        var primary = await page.ResolveColorAsync("var(--mud-palette-primary)");
        Assert.Equal(primary, await GetBackgroundAsync(tile));
    }

    [Fact]
    public async Task WhenSwitchTileMapHasNoCaseForTheValue_ThenItIsFilledWithThePrimaryColorWhileTrue()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "No case");

        // Act
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });

        // Assert
        var primary = await page.ResolveColorAsync("var(--mud-palette-primary)");
        Assert.Equal(primary, await GetBackgroundAsync(tile));
    }

    [Fact]
    public async Task WhenSwitchTileHasNoSubtitle_ThenItShowsTheTextOfTheMatchedCase()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Case text");

        // Act
        await Assertions.Expect(tile).ToBeCheckedAsync(new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Running");
    }

    [Fact]
    public async Task WhenSwitchTilePathLeadsNowhere_ThenItShowsAnIconAndIsDisabled()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var tile = GetSwitchTile(page, "Missing");

        // Assert
        await Assertions.Expect(tile).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("tile-icon")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenTileIsTheOnlyContentOfAGridCell_ThenItFillsTheCell()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var tile = page.GetByTestId("switch-tile").Filter(new() { Has = GetSwitchTile(page, "Plain") });
        await Assertions.Expect(tile).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert
        var tileBox = await PageGeometry.GetBoxAsync(tile);
        var cellBox = await PageGeometry.GetBoxAsync(tile.Locator("xpath=ancestor::div[contains(@class,'grid-cell')][1]"));
        Assert.InRange(cellBox.Width - tileBox.Width, 0, 2.5);
        Assert.InRange(cellBox.Height - tileBox.Height, 0, 2.5);
    }

    [Fact]
    public async Task WhenOperationButtonTileHasNoLabel_ThenItShowsOnlyTheIconAndIsDisabledWhileTheOperationIsNotEnabled()
    {
        // Arrange
        GetMotor().IsPowered = true;
        var page = await OpenPageAsync();
        var tile = page.GetByRole(AriaRole.Button, new() { Name = "Power On", Exact = true });

        // Act & Assert: PowerOn is not enabled while the motor is powered.
        await Assertions.Expect(tile).ToBeDisabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile.GetByTestId("tile-icon")).ToBeVisibleAsync();
        await Assertions.Expect(tile.GetByTestId("tile-label")).ToHaveCountAsync(0);

        GetMotor().IsPowered = false;
        await Assertions.Expect(tile).ToBeEnabledAsync(new() { Timeout = Timeout });
    }
}
