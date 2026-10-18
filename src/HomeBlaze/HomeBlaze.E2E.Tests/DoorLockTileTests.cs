using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the door lock tile.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class DoorLockTileTests
{
    private const int Timeout = 30000;
    private const string PageName = "DoorLock.md";

    private readonly PlaywrightFixture _fixture;

    public DoorLockTileTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private SimulatedDoorLock GetLock() =>
        (SimulatedDoorLock)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["frontdoor"];

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
        var page = await OpenPageAsync();

        // Assert
        await Assertions.Expect(page.GetByTestId("door-lock-tile").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".markdown-block-error")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenLockIsUnlocked_ThenTheTileShowsItAndAClickLocksIt()
    {
        // Arrange
        var doorLock = GetLock();
        doorLock.LockState = DoorLockState.Unlocked;
        doorLock.DoorState = DoorState.Closed;
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
        var doorLock = GetLock();
        doorLock.LockState = DoorLockState.Locked;
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
    public async Task WhenDoorOpens_ThenTheSubtitleSaysSo()
    {
        // Arrange
        var doorLock = GetLock();
        doorLock.LockState = DoorLockState.Unlocked;
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");

        // Act
        doorLock.DoorState = DoorState.Open;

        // Assert
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Unlocked · open", new() { Timeout = Timeout });
        doorLock.DoorState = DoorState.Closed;
    }

    [Fact]
    public async Task WhenLockIsJammed_ThenTheTileIsDisabledAndFilledWithTheErrorColor()
    {
        // Arrange
        GetLock().LockState = DoorLockState.Jammed;
        var page = await OpenPageAsync();
        var tile = GetTile(page, "Front door");

        // Act
        await Assertions.Expect(tile.GetByTestId("tile-subtitle")).ToHaveTextAsync("Jammed", new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(tile).ToBeDisabledAsync();
        var error = await page.ResolveColorAsync("var(--mud-palette-error)");
        var background = await GetTileRoot(tile).EvaluateAsync<string>("element => getComputedStyle(element).backgroundColor");
        Assert.Equal(error, background);
        GetLock().LockState = DoorLockState.Locked;
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
