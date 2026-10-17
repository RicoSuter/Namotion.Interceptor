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

    private async Task<(IPage Page, ILocator Pane)> OpenPaneAsync(string subjectPath)
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 720);
        await page.GotoAsync($"{_fixture.ServerAddress}browser/{subjectPath}");

        // The page is prerendered, and a click before its circuit is interactive is lost.
        await page.WaitUntilInteractiveAsync(PageLoadTimeout);
        return (page, page.Locator("#scrollContainer [data-testid='subject-pane']").Last);
    }

    // The test data is a copy in the output folder. Writing its text back and running a pass loads the metadata
    // the test started with, so that the order and the title it changed do not reach the next test.
    private async Task RestoreDemoMetadataAsync(string metadataPath, string originalMetadata)
    {
        await File.WriteAllTextAsync(metadataPath, originalMetadata);
        await _fixture.ReconcileStorageAsync();
    }

    [Fact]
    public async Task WhenAChildIsMovedUp_ThenThePaneAndTheFolderOrderChange()
    {
        // Arrange
        var metadataPath = _fixture.GetDemoPagePath(".metadata.json");
        var originalMetadata = await File.ReadAllTextAsync(metadataPath);
        var folder = GetDemoFolder();
        try
        {
            var (page, pane) = await OpenPaneAsync("Demo");

            // Only the rows of a collection of an ordered container have move arrows.
            var rows = pane.Locator(".mud-list-item").Filter(new() { Has = page.Locator(".subject-pane-move") });
            await Assertions.Expect(rows.Nth(1)).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
            var firstName = await rows.Nth(0).Locator(".subject-pane-child-name").InnerTextAsync();
            var secondName = await rows.Nth(1).Locator(".subject-pane-child-name").InnerTextAsync();

            // Act
            await rows.Nth(1).HoverAsync();
            await rows.Nth(1).GetByTestId("move-child-up-button").ClickAsync();

            // Assert
            await Assertions.Expect(rows.Nth(0).Locator(".subject-pane-child-name"))
                .ToHaveTextAsync(secondName, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(rows.Nth(1).Locator(".subject-pane-child-name")).ToHaveTextAsync(firstName);

            // The test data lists only Inline.md, so the move writes the whole order with it in second place.
            await AsyncTestHelpers.WaitUntilAsync(
                () => folder.ChildOrder.Length > 1 && folder.ChildOrder[1] == "Inline.md",
                message: "The order of the folder was not written");
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
