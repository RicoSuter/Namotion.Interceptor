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

    // The test data is a copy in the output folder. Writing its text back and running a pass loads the metadata
    // the test started with, so that the order and the title it changed do not reach the next test.
    private async Task RestoreDemoMetadataAsync(string metadataPath, string originalMetadata)
    {
        await File.WriteAllTextAsync(metadataPath, originalMetadata);
        await _fixture.ReconcileStorageAsync();
    }

    [Fact]
    public async Task WhenAChildIsDraggedToTheTop_ThenThePaneAndTheFolderOrderChange()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (_, pane) = await OpenPaneAsync("Demo");
            var rows = await StartReorderingAsync(pane);
            var shownNames = await rows.Locator(".subject-pane-child-name").AllInnerTextsAsync();
            var names = GetShownChildNames(folder);
            Assert.Equal(names.Length, shownNames.Count);

            // Act - a drop on a row puts the child after it, and one on the strip at the top of the list in first place.
            await rows.Nth(2).Locator(".mud-drag-handle").DragToAsync(rows.Nth(0), new()
            {
                TargetPosition = new() { X = 100, Y = 2 },

                // The strip lies over the first row, which Playwright reports as covered.
                Force = true
            });

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
            await Assertions.Expect(rows.Nth(1).Locator(".mud-drag-handle")).ToBeHiddenAsync();

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
