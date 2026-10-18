using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the order of folders and the metadata of folders in the subject browser and the navigation.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class NavigationMetadataTests
{
    private const int PageLoadTimeout = 30000;

    private readonly PlaywrightFixture _fixture;

    public NavigationMetadataTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private VirtualFolder GetDemoFolder()
    {
        var root = (FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        return (VirtualFolder)root.Children["Demo"];
    }

    // The names of the children of the folder in the order in which the pane shows them.
    private static string[] GetShownChildNames(VirtualFolder folder)
        => ChildOrdering
            .Order(folder.Children, folder.ChildOrder, static child => child.Key, static child => child.Value.GetNavigationTitle(child.Key))
            .Select(static child => child.Key)
            .ToArray();

    private async Task<(IPage Page, ILocator Pane)> OpenPaneAsync(
        string subjectPath, BrowserNewContextOptions? contextOptions = null)
    {
        var page = await _fixture.CreatePageAsync(contextOptions);
        if (contextOptions?.ViewportSize is null)
        {
            await page.SetViewportSizeAsync(1280, 720);
        }

        await page.GotoAsync($"{_fixture.ServerAddress}browser/{subjectPath}");

        // The page is prerendered, and a click before its circuit is interactive is lost.
        await page.WaitUntilInteractiveAsync(PageLoadTimeout);
        return (page, page.Locator("#scrollContainer [data-testid='subject-pane']").Last);
    }

    private static async Task<ILocator> StartReorderingAsync(ILocator pane)
    {
        await pane.GetByTestId("reorder-children-button").ClickAsync();
        await Assertions.Expect(pane.GetByTestId("reorder-children-button")).ToHaveAttributeAsync("aria-label", "Done");

        var rows = pane.GetByTestId("reorder-child-row");
        await Assertions.Expect(rows.Nth(2)).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        return rows;
    }

    // Drags a row by its middle with the mouse to a point of the target row, given as a fraction of its height, in
    // steps, as a drag starts only once the pointer has moved a few pixels. Leaves the button pressed.
    private static async Task MoveDraggedRowAsync(IPage page, ILocator rows, int index, int targetIndex, float targetFraction)
    {
        var row = await rows.Nth(index).BoundingBoxAsync() ?? throw new InvalidOperationException("The row is not shown.");
        var target = await rows.Nth(targetIndex).BoundingBoxAsync() ?? throw new InvalidOperationException("The target row is not shown.");
        var x = row.X + row.Width / 2;

        await page.Mouse.MoveAsync(x, row.Y + row.Height / 2);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(x, target.Y + target.Height * targetFraction, new() { Steps = 10 });
        await Assertions.Expect(page.Locator(".subject-pane-reorder-preview")).ToBeVisibleAsync();
    }

    private static async Task DragRowAsync(IPage page, ILocator rows, int index, int targetIndex, float targetFraction)
    {
        await MoveDraggedRowAsync(page, rows, index, targetIndex, targetFraction);
        await page.Mouse.UpAsync();
    }

    // The test data is a copy in the output folder. Writing its text back and running a pass loads the metadata
    // the test started with, so that the order and the title it changed do not reach the next test.
    private async Task RestoreDemoMetadataAsync(string metadataPath, string originalMetadata)
    {
        await File.WriteAllTextAsync(metadataPath, originalMetadata);
        await _fixture.ReconcileStorageAsync();
    }

    [Fact]
    public async Task WhenAChildIsDroppedOnTheUpperHalfOfTheFirstRow_ThenItGoesFirst()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            Assert.Equal(names.Length, shownNames.Count);

            // Act
            await DragRowAsync(page, rows, index: 2, targetIndex: 0, targetFraction: 0.25f);

            // Assert
            string[] expectedShownNames = [shownNames[2], shownNames[0], shownNames[1], .. shownNames.Skip(3)];
            await Assertions.Expect(rows.Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(expectedShownNames, new() { Timeout = PageLoadTimeout });

            string[] expectedNames = [names[2], names[0], names[1], .. names.Skip(3)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenAChildIsDroppedOnTheLowerHalfOfARow_ThenItLandsBelowIt()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            Assert.Equal(names.Length, shownNames.Count);

            // Act
            await DragRowAsync(page, rows, index: 0, targetIndex: 2, targetFraction: 0.75f);

            // Assert
            string[] expectedShownNames = [shownNames[1], shownNames[2], shownNames[0], .. shownNames.Skip(3)];
            await Assertions.Expect(rows.Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(expectedShownNames, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(page.Locator(".subject-pane-reorder-preview")).ToHaveCountAsync(0);

            string[] expectedNames = [names[1], names[2], names[0], .. names.Skip(3)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenAChildIsDroppedOnTheUpperHalfOfALaterRow_ThenItLandsAboveIt()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            Assert.Equal(names.Length, shownNames.Count);

            // Act
            await DragRowAsync(page, rows, index: 0, targetIndex: 3, targetFraction: 0.25f);

            // Assert
            string[] expectedShownNames = [shownNames[1], shownNames[2], shownNames[0], .. shownNames.Skip(3)];
            await Assertions.Expect(rows.Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(expectedShownNames, new() { Timeout = PageLoadTimeout });

            string[] expectedNames = [names[1], names[2], names[0], .. names.Skip(3)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenAChildIsDroppedOnItsOwnPlace_ThenTheOrderStays()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);

            // Act - the lower half of the row above the dragged one is the place of the dragged one.
            await DragRowAsync(page, rows, index: 1, targetIndex: 0, targetFraction: 0.75f);

            // Assert
            await Assertions.Expect(page.Locator(".subject-pane-reorder-preview")).ToHaveCountAsync(0);
            await Assertions.Expect(rows.Locator(".subject-pane-child-name")).ToHaveTextAsync(shownNames.ToArray());
            Assert.Equal(originalMetadata, await File.ReadAllTextAsync(metadataPath));

            // A move after it shows the order the drop left, as a write of the drop would come before the move's.
            await DragRowAsync(page, rows, index: 0, targetIndex: 2, targetFraction: 0.75f);
            string[] expectedNames = [names[1], names[2], names[0], .. names.Skip(3)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenEscapeIsPressedDuringADrag_ThenTheReleaseDropsNothing()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            await MoveDraggedRowAsync(page, rows, index: 0, targetIndex: 3, targetFraction: 0.75f);

            // Act
            await page.Keyboard.PressAsync("Escape");
            await page.Mouse.UpAsync();

            // Assert
            await Assertions.Expect(page.Locator(".subject-pane-reorder-preview")).ToHaveCountAsync(0);
            await Assertions.Expect(page.Locator(".subject-pane-reorder-indicator")).ToHaveCountAsync(0);
            await Assertions.Expect(rows.Locator(".subject-pane-child-name")).ToHaveTextAsync(shownNames.ToArray());

            // A move after it shows the order the release left, as a write of the release would come before the move's.
            await DragRowAsync(page, rows, index: 2, targetIndex: 0, targetFraction: 0.25f);
            string[] expectedNames = [names[2], names[0], names[1], .. names.Skip(3)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenEnterIsPressedTwiceOnAnArrow_ThenTheRowMovesTwoPlaces()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            var upArrow = pane.GetByRole(AriaRole.Button, new() { Name = $"Move {shownNames[3]} up", Exact = true });
            await upArrow.FocusAsync();

            // Act - the second press goes to the arrow that has the focus after the first move.
            await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(rows.Nth(2).Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(shownNames[3], new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(upArrow).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Enter");

            // Assert
            string[] expectedShownNames = [shownNames[0], shownNames[3], shownNames[1], shownNames[2], .. shownNames.Skip(4)];
            await Assertions.Expect(rows.Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(expectedShownNames, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(upArrow).ToBeFocusedAsync();

            string[] expectedNames = [names[0], names[3], names[1], names[2], .. names.Skip(4)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenARowIsMovedToTheTopWithEnter_ThenItsDownArrowHasTheFocus()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            await pane.GetByRole(AriaRole.Button, new() { Name = $"Move {shownNames[1]} up", Exact = true }).FocusAsync();

            // Act
            await page.Keyboard.PressAsync("Enter");

            // Assert - the first row has no up arrow, so the one that was pressed is gone.
            await Assertions.Expect(rows.Nth(0).Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(shownNames[1], new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(rows.Nth(0).GetByTestId("move-child-up-button")).ToHaveCountAsync(0);
            await Assertions.Expect(pane.GetByRole(AriaRole.Button, new() { Name = $"Move {shownNames[1]} down", Exact = true }))
                .ToBeFocusedAsync(new() { Timeout = PageLoadTimeout });

            string[] expectedNames = [names[1], names[0], .. names.Skip(2)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenTheDeviceHasNoHover_ThenTheArrowsShowAndMoveAChild()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo", new BrowserNewContextOptions
            {
                HasTouch = true,
                IsMobile = true,
                ViewportSize = new() { Width = 390, Height = 844 }
            });
            Assert.True(
                await page.EvaluateAsync<bool>("() => matchMedia('(hover: none)').matches"),
                "The browser context reports hover, so it does not stand for a touch device.");

            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);

            // Playwright counts a transparent element as visible, so the arrows are checked by their opacity.
            await Assertions.Expect(rows.Nth(1).Locator(".subject-pane-move")).ToHaveCSSAsync("opacity", "1");

            // Act
            await rows.Nth(1).GetByTestId("move-child-up-button").TapAsync();

            // Assert
            await Assertions.Expect(rows.Nth(0).Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(shownNames[1], new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(rows.Nth(1).Locator(".subject-pane-child-name")).ToHaveTextAsync(shownNames[0]);

            string[] expectedNames = [names[1], names[0], .. names.Skip(2)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenTheHandleIsDraggedByTouch_ThenTheRowMoves()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo", new BrowserNewContextOptions
            {
                HasTouch = true,
                IsMobile = true,
                ViewportSize = new() { Width = 390, Height = 844 }
            });

            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            var handle = await rows.Nth(0).Locator(".subject-pane-reorder-handle").BoundingBoxAsync()
                ?? throw new InvalidOperationException("The handle is not shown.");
            var target = await rows.Nth(2).BoundingBoxAsync()
                ?? throw new InvalidOperationException("The target row is not shown.");

            // Playwright taps but does not swipe, so the touches go through the protocol of Chromium.
            var session = await page.Context.NewCDPSessionAsync(page);
            Task TouchAsync(string type, double x, double y) => session.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
            {
                ["type"] = type,
                ["touchPoints"] = type == "touchEnd" ? Array.Empty<object>() : new object[] { new Dictionary<string, object> { ["x"] = x, ["y"] = y } }
            });

            var x = handle.X + handle.Width / 2;
            var y = handle.Y + handle.Height / 2;
            var targetY = target.Y + target.Height * 0.75f;

            // Act
            await TouchAsync("touchStart", x, y);
            for (var step = 1; step <= 10; step++)
            {
                await TouchAsync("touchMove", x, y + (targetY - y) * step / 10);
            }

            await Assertions.Expect(page.Locator(".subject-pane-reorder-preview")).ToBeVisibleAsync();
            await TouchAsync("touchEnd", x, targetY);

            // Assert
            string[] expectedShownNames = [shownNames[1], shownNames[2], shownNames[0], .. shownNames.Skip(3)];
            await Assertions.Expect(rows.Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(expectedShownNames, new() { Timeout = PageLoadTimeout });

            string[] expectedNames = [names[1], names[2], names[0], .. names.Skip(3)];
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
                message: "The order of the folder was not written");
            Assert.Equal(expectedNames, folder.ChildOrder.ToArray());
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }

    [Fact]
    public async Task WhenFolderTitleIsSaved_ThenThePaneAndTheNavigationShowIt()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");
            await pane.GetByTestId("edit-metadata-button").ClickAsync();

            // MudDialog puts its test id on its content, which does not hold the actions.
            var dialog = page.Locator(".mud-dialog").Filter(new() { Has = page.GetByTestId("metadata-edit-dialog") });
            await dialog.GetByTestId("metadata-title").FillAsync("Demonstration");

            // Act
            await dialog.GetByTestId("metadata-save").ClickAsync();

            // Assert
            await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(pane.Locator("h1")).ToContainTextAsync("Demonstration", new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(page.Locator(".mud-navmenu").GetByRole(AriaRole.Button, new() { Name = "Demonstration" }))
                .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            Assert.Equal("Demonstration", GetDemoFolder().Metadata?.Title);
        }
        finally
        {
            await RestoreDemoMetadataAsync(metadataPath, originalMetadata);
        }
    }
}
