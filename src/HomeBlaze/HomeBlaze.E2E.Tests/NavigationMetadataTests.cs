using System.Text.Json;
using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;
using NavigationLocation = HomeBlaze.Components.Abstractions.Pages.NavigationLocation;

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
    private static string[] GetShownChildNames(VirtualFolder folder) => ChildOrdering.GetShownNames(folder);

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
    private async Task WithDemoMetadataAsync(Func<VirtualFolder, Task> test)
    {
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        try
        {
            await test(GetDemoFolder());
        }
        finally
        {
            await RestoreFileAsync(metadataPath, originalMetadata);
        }
    }

    // Writes the text back, or deletes a file that did not exist before the test, and runs a pass.
    private async Task RestoreFileAsync(string path, string? originalText)
    {
        if (originalText is null)
        {
            File.Delete(path);
        }
        else
        {
            await File.WriteAllTextAsync(path, originalText);
        }

        await _fixture.ReconcileStorageAsync();
    }

    private static Task WaitForOrderAsync(VirtualFolder folder, string[] expectedNames)
        => AsyncTestHelpers.WaitUntilAsync(
            () => folder.ChildOrder.ToArray().SequenceEqual(expectedNames),
            message: "The order of the folder was not written");

    [Theory]
    [InlineData(2, 0, 0.25f, new[] { 2, 0, 1 })]
    [InlineData(0, 2, 0.75f, new[] { 1, 2, 0 })]
    [InlineData(0, 3, 0.25f, new[] { 1, 2, 0 })]
    public Task WhenAChildIsDropped_ThenItLandsOnTheSideOfTheHoveredRowWhereThePointerIs(
        int index, int targetIndex, float targetFraction, int[] firstThree) => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync("Demo");
        var rows = await StartReorderingAsync(pane);
        var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
        var names = GetShownChildNames(folder);
        Assert.Equal(names.Length, shownNames.Count);

        // Act
        await DragRowAsync(page, rows, index, targetIndex, targetFraction);

        // Assert
        string[] expectedShownNames = [.. firstThree.Select(position => shownNames[position]), .. shownNames.Skip(3)];
        await Assertions.Expect(rows.Locator(".subject-pane-child-name"))
            .ToHaveTextAsync(expectedShownNames, new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(page.Locator(".subject-pane-reorder-preview")).ToHaveCountAsync(0);
        await WaitForOrderAsync(folder, [.. firstThree.Select(position => names[position]), .. names.Skip(3)]);
    });

    [Fact]
    public Task WhenAChildIsDroppedOnItsOwnPlace_ThenTheOrderStays() => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
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
        await WaitForOrderAsync(folder, [names[1], names[2], names[0], .. names.Skip(3)]);
    });

    [Fact]
    public Task WhenEscapeIsPressedDuringADrag_ThenTheReleaseDropsNothing() => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
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
        await WaitForOrderAsync(folder, [names[2], names[0], names[1], .. names.Skip(3)]);
    });

    [Fact]
    public Task WhenEnterIsPressedTwiceOnAnArrow_ThenTheRowMovesTwoPlaces() => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
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
        await WaitForOrderAsync(folder, [names[0], names[3], names[1], names[2], .. names.Skip(4)]);
    });

    [Fact]
    public Task WhenARowIsMovedToTheTopWithEnter_ThenItsDownArrowHasTheFocus() => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
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
        await WaitForOrderAsync(folder, [names[1], names[0], .. names.Skip(2)]);
    });

    [Fact]
    public Task WhenTheDeviceHasNoHover_ThenTheArrowsShowAndMoveAChild() => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
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
        await WaitForOrderAsync(folder, [names[1], names[0], .. names.Skip(2)]);
    });

    // Drags the handle of a row by touch to a point of the target row, given as a fraction of its height. Playwright
    // taps but does not swipe, so the touches go through the protocol of Chromium.
    private static async Task DragHandleByTouchAsync(
        IPage page, ILocator rows, int index, int targetIndex, float targetFraction, bool isLongPressed)
    {
        var handle = rows.Nth(index).Locator(".subject-pane-reorder-handle");
        var handleBox = await handle.BoundingBoxAsync() ?? throw new InvalidOperationException("The handle is not shown.");
        var target = await rows.Nth(targetIndex).BoundingBoxAsync() ?? throw new InvalidOperationException("The target row is not shown.");
        var session = await page.Context.NewCDPSessionAsync(page);
        try
        {
            Task TouchAsync(string type, double x, double y) => session.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
            {
                ["type"] = type,
                ["touchPoints"] = type == "touchEnd" ? Array.Empty<object>() : new object[] { new Dictionary<string, object> { ["x"] = x, ["y"] = y } }
            });

            var x = handleBox.X + handleBox.Width / 2;
            var y = handleBox.Y + handleBox.Height / 2;
            var targetY = target.Y + target.Height * targetFraction;

            await TouchAsync("touchStart", x, y);
            if (isLongPressed)
            {
                // Chrome on Android opens the context menu of a held touch, which headless Chromium does not do for
                // touches of the protocol, so the test sends it while the touch is held.
                var isContextMenuSuppressed = await handle.EvaluateAsync<bool>(
                    "element => !element.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true }))");
                Assert.True(isContextMenuSuppressed, "The context menu of the long press was not suppressed.");
            }

            for (var step = 1; step <= 10; step++)
            {
                await TouchAsync("touchMove", x, y + (targetY - y) * step / 10);
            }

            await Assertions.Expect(page.Locator(".subject-pane-reorder-preview")).ToBeVisibleAsync();
            await TouchAsync("touchEnd", x, targetY);
        }
        finally
        {
            await session.DetachAsync();
        }
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task WhenTheHandleIsDraggedByTouch_ThenTheRowMoves(bool isLongPressed) => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
        var (page, pane) = await OpenPaneAsync("Demo", new BrowserNewContextOptions
        {
            HasTouch = true,
            IsMobile = true,
            ViewportSize = new() { Width = 390, Height = 844 }
        });

        var rows = await StartReorderingAsync(pane);
        var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
        var names = GetShownChildNames(folder);

        // Act
        await DragHandleByTouchAsync(page, rows, index: 0, targetIndex: 2, targetFraction: 0.75f, isLongPressed);

        // Assert
        string[] expectedShownNames = [shownNames[1], shownNames[2], shownNames[0], .. shownNames.Skip(3)];
        await Assertions.Expect(rows.Locator(".subject-pane-child-name"))
            .ToHaveTextAsync(expectedShownNames, new() { Timeout = PageLoadTimeout });
        await WaitForOrderAsync(folder, [names[1], names[2], names[0], .. names.Skip(3)]);
    });

    [Fact]
    public Task WhenFolderTitleIsSaved_ThenThePaneAndTheNavigationShowIt() => WithDemoMetadataAsync(async folder =>
    {
        // Arrange
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
        Assert.Equal("Demonstration", folder.Title);
    });

    [Fact]
    public async Task WhenTheStorageRootIsShown_ThenItHasNoMetadataButton()
    {
        // Act
        var (_, pane) = await OpenPaneAsync("");

        // Assert
        await Assertions.Expect(pane.GetByTestId("reorder-children-button")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane.GetByTestId("edit-metadata-button")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAFolderWithMetadataIsShown_ThenEachChildHasOneRowAndTheMetadataIsNotListed()
    {
        // Arrange
        var folder = GetDemoFolder();
        Assert.NotNull(folder.GetMetadata());

        // Act
        var (_, pane) = await OpenPaneAsync("Demo");

        // Assert
        await Assertions.Expect(pane.Locator(".subject-pane-child-name"))
            .ToHaveCountAsync(folder.Children.Count, new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(pane).Not.ToContainTextAsync("HomeBlaze:Metadata");
    }

    [Fact]
    public async Task WhenAnAgentWritesTheFirstMetadataOfAFolderWhileItsPaneIsOpen_ThenThePaneShowsIt()
    {
        // Arrange
        var root = (FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        var folder = (VirtualFolder)root.Children["Agents"];
        var metadataPath = _fixture.GetPagePath(Path.Combine("Agents", ".metadata.json"));
        Assert.False(File.Exists(metadataPath), "The folder has metadata already.");
        Assert.Null(folder.GetMetadata());
        try
        {
            var (_, pane) = await OpenPaneAsync("Agents");
            await Assertions.Expect(pane.Locator("h1")).ToContainTextAsync("Agents", new() { Timeout = PageLoadTimeout });

            // Act - the method that agents call.
            await root.SetMetadataAsync("Agents", null, JsonElement.Parse("""{ "title": "Assistants" }"""), CancellationToken.None);

            // Assert - without a reload.
            await Assertions.Expect(pane.Locator("h1")).ToContainTextAsync("Assistants", new() { Timeout = PageLoadTimeout });
            Assert.Equal("Assistants", folder.Title);
        }
        finally
        {
            await RestoreFileAsync(metadataPath, null);
        }
    }

    [Fact]
    public async Task WhenASubjectInsideAStoredSubjectGetsANavigationTitle_ThenTheFileBesideTheStoredSubjectHasItsEntry()
    {
        // Arrange
        var sidecarPath = _fixture.GetDemoPagePath("Layout.metadata.json");
        var originalSidecar = File.Exists(sidecarPath) ? await File.ReadAllTextAsync(sidecarPath) : null;
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo/Layout/Cells/0");
            await pane.GetByTestId("edit-metadata-button").ClickAsync();

            // MudDialog puts its test id on its content, which does not hold the actions.
            var dialog = page.Locator(".mud-dialog").Filter(new() { Has = page.GetByTestId("metadata-edit-dialog") });
            await dialog.GetByTestId("metadata-navigation-title").FillAsync("First cell");

            // Act
            await dialog.GetByTestId("metadata-save").ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = PageLoadTimeout });

            // Assert
            var cell = ((HomeBlaze.Components.GridLayout)GetDemoFolder().Children["Layout"]).Cells[0];
            Assert.Equal("First cell", cell.GetNavigationTitle());
            using var sidecar = JsonDocument.Parse(await File.ReadAllTextAsync(sidecarPath));
            Assert.Equal("First cell", sidecar.RootElement
                .GetProperty("$subjects").GetProperty("Cells[0]").GetProperty("navTitle").GetString());
        }
        finally
        {
            await RestoreFileAsync(sidecarPath, originalSidecar);
        }
    }

    [Fact]
    public async Task WhenAPropertyHasMetadata_ThenItsRowGetsNoDetails()
    {
        // Arrange
        var motor = GetDemoFolder().Children["Motor"];
        var sidecarPath = _fixture.GetDemoPagePath("Motor.metadata.json");
        var originalSidecar = File.Exists(sidecarPath) ? await File.ReadAllTextAsync(sidecarPath) : null;
        try
        {
            await motor.FindMetadataStore()!.SetMetadataAsync(
                motor, "Status", JsonElement.Parse("""{ "comment": "Reported by the controller" }"""), CancellationToken.None);

            // Act
            var (page, pane) = await OpenPaneAsync("Demo/Motor");

            // Assert
            var statusRow = pane.GetByTestId("subject-pane-value")
                .Filter(new() { Has = page.GetByTestId("subject-pane-value-name").Filter(new() { HasTextRegex = new Regex("^Status$") }) });
            await Assertions.Expect(statusRow).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(statusRow.GetByTestId("subject-pane-value-expand")).ToHaveCountAsync(0);
            await Assertions.Expect(pane).Not.ToContainTextAsync("ImmutableDictionary");
        }
        finally
        {
            await RestoreFileAsync(sidecarPath, originalSidecar);
        }
    }

    [Fact]
    public async Task WhenSubjectInFolderIsMovedToTheTopBar_ThenTheTopBarShowsIt()
    {
        // Arrange
        var root = (FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        var folder = (VirtualFolder)root.Children["Agents"];
        var agent = folder.Children["TestAgent"];
        var sidecarPath = _fixture.GetPagePath(Path.Combine("Agents", "TestAgent.metadata.json"));
        var originalSidecar = File.Exists(sidecarPath) ? await File.ReadAllTextAsync(sidecarPath) : null;
        try
        {
            var (page, pane) = await OpenPaneAsync("Agents/TestAgent");
            await pane.GetByTestId("edit-metadata-button").ClickAsync();

            // MudDialog puts its test id on its content, which does not hold the actions.
            var dialog = page.Locator(".mud-dialog").Filter(new() { Has = page.GetByTestId("metadata-edit-dialog") });
            await dialog.GetByTestId("metadata-location").ClickAsync();
            await page.Locator(".mud-popover-open").GetByText("Top bar", new() { Exact = true }).ClickAsync();

            // Act
            await dialog.GetByTestId("metadata-save").ClickAsync();
            await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = PageLoadTimeout });

            // Assert - the top bar refreshes on its own, without a navigation.
            await Assertions.Expect(page.GetByRole(AriaRole.Banner).Locator("a[href='pages/Agents/TestAgent']"))
                .ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            Assert.Equal(NavigationLocation.AppBar, agent.GetNavigationLocation());
        }
        finally
        {
            await RestoreFileAsync(sidecarPath, originalSidecar);
        }
    }
}
