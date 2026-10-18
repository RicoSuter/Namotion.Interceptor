using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the door lock tile. The tests share one lock, so each one sets the state it needs and the lock is
/// reset after each test, also when it fails.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class DoorLockTileTests : IDisposable
{
    private const int Timeout = 30000;
    private const string PageName = "DoorLock.md";

    private readonly PlaywrightFixture _fixture;
    private readonly TimeSpan _originalTransitionDuration;

    public DoorLockTileTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
        _originalTransitionDuration = GetLock().TransitionDuration;
    }

    public void Dispose()
    {
        GetLock().TransitionDuration = _originalTransitionDuration;
        SetLockState(DoorLockState.Locked, DoorState.Closed);
    }

    private SimulatedDoorLock GetLock() =>
        (SimulatedDoorLock)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["frontdoor"];

    private SimulatedDoorLock SetLockState(DoorLockState lockState, DoorState doorState)
    {
        var doorLock = GetLock();
        doorLock.LockState = lockState;
        doorLock.DoorState = doorState;
        return doorLock;
    }

    private async Task<IPage> OpenPageAsync(int? width = null)
    {
        var page = await _fixture.CreatePageAsync();

        // Before the page opens: a narrower window closes the navigation drawer with a transition.
        if (width is { } viewportWidth)
        {
            await page.SetViewportSizeAsync(viewportWidth, 844);
        }

        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    // Sets the lock state with the door closed, opens the page and waits until the tile of the lock shows the state.
    private async Task<(IPage Page, ILocator Tile)> OpenTileAsync(DoorLockState lockState, string expectedSubtitle)
    {
        SetLockState(lockState, DoorState.Closed);
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync(expectedSubtitle, new() { Timeout = Timeout });
        return (page, tile);
    }

    private static ILocator GetTile(IPage page, string label) =>
        page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });

    private static ILocator GetTileRoot(ILocator tileButton) => tileButton.Locator("xpath=..");

    private static ILocator GetConfirmationDialog(IPage page) =>
        page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "Are you sure you want to execute this operation?" });

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrorsAndEveryTileShowsItsBadge()
    {
        // Arrange & Act
        SetLockState(DoorLockState.Locked, DoorState.Closed);
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("door-lock-tile").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
        await Assertions.Expect(GetTileRoot(GetTile(page, "Front door")).GetByTestId("tile-badge")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetTileRoot(GetTile(page, "Missing")).GetByTestId("tile-badge")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenLockIsLocked_ThenTheAccessibleDescriptionNamesTheStateAndTheClickAndTheTooltipNamesTheOperation()
    {
        // Arrange & Act
        var (_, tile) = await OpenTileAsync(DoorLockState.Locked, "Locked");

        // Assert
        await Assertions.Expect(tile).ToHaveAccessibleDescriptionAsync("Locked, unlocks on click", new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToHaveAttributeAsync("title", "Unlock");
    }

    [Fact]
    public async Task WhenLockedTileIsClicked_ThenUnlockAsksForConfirmationFirst()
    {
        // Arrange
        var (page, tile) = await OpenTileAsync(DoorLockState.Locked, "Locked");

        // Act
        await tile.ClickAsync();
        var dialog = GetConfirmationDialog(page);
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Timeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();

        // Assert
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = Timeout });
        Assert.Equal(DoorLockState.Locked, GetLock().LockState);
    }

    [Fact]
    public async Task WhenLockMoves_ThenTheTileShowsTheTransitionAndIsBusyUntilItEnds()
    {
        // Arrange
        GetLock().TransitionDuration = TimeSpan.FromSeconds(3);
        var (_, tile) = await OpenTileAsync(DoorLockState.Unlocked, "Unlocked");

        // Act
        await tile.ClickAsync();

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Locking…", new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToBeDisabledAsync();
        await Assertions.Expect(GetTileRoot(tile).GetByTestId("tile-progress")).ToBeVisibleAsync();
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Locked", new() { Timeout = Timeout });
        await Assertions.Expect(GetTileRoot(tile).GetByTestId("tile-progress")).ToBeHiddenAsync(new() { Timeout = Timeout });
        Assert.Equal(DoorLockState.Locked, GetLock().LockState);
    }

    [Fact]
    public async Task WhenUnlockIsConfirmed_ThenTheLockIsUnlocked()
    {
        // Arrange
        var (page, tile) = await OpenTileAsync(DoorLockState.Locked, "Locked");

        // Act
        await tile.ClickAsync();
        var dialog = GetConfirmationDialog(page);
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Timeout });
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Execute", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked", new() { Timeout = Timeout });
        Assert.Equal(DoorLockState.Unlocked, GetLock().LockState);
    }

    [Fact]
    public async Task WhenDoorOpens_ThenTheSubtitleSaysSo()
    {
        // Arrange
        var (_, tile) = await OpenTileAsync(DoorLockState.Unlocked, "Unlocked");

        // Act
        GetLock().DoorState = DoorState.Open;

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked · open", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenLockIsJammed_ThenTheTileIsFilledWithTheErrorColorAndOffersToLock()
    {
        // Arrange & Act
        var (page, tile) = await OpenTileAsync(DoorLockState.Jammed, "Jammed");

        // Assert
        await Assertions.Expect(tile).ToBeEnabledAsync(new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToHaveAccessibleDescriptionAsync("Jammed, locks on click", new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToHaveAttributeAsync("title", "Lock");
        var error = await page.ResolveColorAsync("var(--mud-palette-error)");
        var background = await GetTileRoot(tile).EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
        Assert.Equal(error, background);
    }

    [Fact]
    public async Task WhenJammedTileIsClicked_ThenTheLockIsLockedWithoutConfirmation()
    {
        // Arrange
        var (page, tile) = await OpenTileAsync(DoorLockState.Jammed, "Jammed");

        // Act
        await tile.ClickAsync();

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Locked", new() { Timeout = Timeout });
        await Assertions.Expect(GetConfirmationDialog(page)).ToHaveCountAsync(0);
        Assert.Equal(DoorLockState.Locked, GetLock().LockState);
    }

    [Fact]
    public async Task WhenTileIsTooSmallForItsText_ThenTheTextStaysBelowTheBadge()
    {
        // Arrange
        SetLockState(DoorLockState.Unlocked, DoorState.Closed);

        // Act: four square columns on a phone give tiles of about 80 pixels, in which the label wraps.
        var page = await OpenPageAsync(width: 390);
        var tile = GetTile(page, "Small front door");
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked", new() { Timeout = Timeout });

        // Assert
        var badge = await PageGeometry.GetBoxAsync(GetTileRoot(tile).GetByTestId("tile-badge").Locator("svg"));
        var label = await PageGeometry.GetBoxAsync(tile.GetByTestId("tile-label"));
        Assert.True(label.Top >= badge.Bottom, $"The label starts at {label.Top}, above the bottom of the badge at {badge.Bottom}.");
    }

    [Fact]
    public async Task WhenPathLeadsNowhere_ThenTheTileIsDisabled()
    {
        // Arrange & Act
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetTile(page, "Missing")).ToBeDisabledAsync(new() { Timeout = Timeout });
    }
}
