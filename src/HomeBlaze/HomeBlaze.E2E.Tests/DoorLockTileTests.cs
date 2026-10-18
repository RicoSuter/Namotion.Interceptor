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

    // The TransitionDuration of the lock on the page.
    private static readonly TimeSpan PageTransitionDuration = TimeSpan.FromMilliseconds(500);

    private readonly PlaywrightFixture _fixture;

    public DoorLockTileTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        // The duration is a configuration property, so it is only written back when a test changed it.
        var doorLock = GetLock();
        if (doorLock.TransitionDuration != PageTransitionDuration)
        {
            doorLock.TransitionDuration = PageTransitionDuration;
        }

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

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    private static ILocator GetTile(IPage page, string label) =>
        page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true });

    private static ILocator GetTileRoot(ILocator tileButton) => tileButton.Locator("xpath=..");

    [Fact]
    public async Task WhenPageRenders_ThenItShowsNoBlockErrors()
    {
        // Arrange & Act
        SetLockState(DoorLockState.Locked, DoorState.Closed);
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("door-lock-tile").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenPageRenders_ThenEveryTileShowsItsBadge()
    {
        // Arrange & Act
        SetLockState(DoorLockState.Locked, DoorState.Closed);
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(GetTileRoot(GetTile(page, "Front door")).GetByTestId("tile-badge")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetTileRoot(GetTile(page, "Missing")).GetByTestId("tile-badge")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenLockIsLocked_ThenTheAccessibleDescriptionNamesTheStateAndTheClickAndTheTooltipNamesTheOperation()
    {
        // Arrange & Act
        SetLockState(DoorLockState.Locked, DoorState.Closed);
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");

        // Assert
        await Assertions.Expect(tile).ToHaveAccessibleDescriptionAsync("Locked, unlocks on click", new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToHaveAttributeAsync("title", "Unlock");
    }

    [Fact]
    public async Task WhenLockIsUnlocked_ThenTheTileShowsItAndAClickLocksIt()
    {
        // Arrange
        SetLockState(DoorLockState.Unlocked, DoorState.Closed);
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked", new() { Timeout = Timeout });

        // Act
        await tile.ClickAsync();

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Locked", new() { Timeout = Timeout });
        Assert.Equal(DoorLockState.Locked, GetLock().LockState);
    }

    [Fact]
    public async Task WhenLockedTileIsClicked_ThenUnlockAsksForConfirmationFirst()
    {
        // Arrange
        SetLockState(DoorLockState.Locked, DoorState.Closed);
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Locked", new() { Timeout = Timeout });

        // Act
        await tile.ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "Are you sure you want to execute this operation?" });
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
        var doorLock = SetLockState(DoorLockState.Unlocked, DoorState.Closed);
        doorLock.TransitionDuration = TimeSpan.FromSeconds(3);
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked", new() { Timeout = Timeout });

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
        SetLockState(DoorLockState.Locked, DoorState.Closed);
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Locked", new() { Timeout = Timeout });

        // Act
        await tile.ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog).Filter(new() { HasText = "Are you sure you want to execute this operation?" });
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
        var doorLock = SetLockState(DoorLockState.Unlocked, DoorState.Closed);
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked", new() { Timeout = Timeout });

        // Act
        doorLock.DoorState = DoorState.Open;

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked · open", new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenLockIsJammed_ThenTheTileIsDisabledAndFilledWithTheErrorColor()
    {
        // Arrange
        SetLockState(DoorLockState.Jammed, DoorState.Closed);

        // Act
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Jammed", new() { Timeout = Timeout });
        await Assertions.Expect(tile).ToBeDisabledAsync();
        var error = await page.ResolveColorAsync("var(--mud-palette-error)");
        var background = await GetTileRoot(tile).EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
        Assert.Equal(error, background);
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
