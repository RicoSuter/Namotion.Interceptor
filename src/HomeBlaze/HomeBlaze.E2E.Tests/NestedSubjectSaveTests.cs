using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for saving a subject that is nested in the configuration of a subject stored as its own file.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class NestedSubjectSaveTests
{
    private const string PagePath = "Demo/LayoutFile.md";
    private const string LayoutPath = "Demo/Layout.json";

    private readonly PlaywrightFixture _fixture;

    public NestedSubjectSaveTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenCellOfLayoutFileIsSaved_ThenLayoutFileIsWritten()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var layoutFilePath = Path.Combine(rootManager.DataDirectory, "TestData", LayoutPath);
        var originalContent = await File.ReadAllTextAsync(layoutFilePath);
        var cell = GetLayoutSubject(rootManager).Cells.Single();
        var originalColumnSpan = cell.ColumnSpan;

        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editMenu.ClickAsync();
            await page.GetByText("Inline").ClickAsync();

            var editCellButton = page.GetByTitle("Edit Cell");
            await Assertions.Expect(editCellButton).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editCellButton.ClickAsync();

            var dialog = page.Locator(".mud-dialog");
            var columnSpanField = dialog.GetByLabel("Column Span");
            await Assertions.Expect(columnSpanField).ToBeVisibleAsync(new() { Timeout = 30000 });
            await columnSpanField.FillAsync("3");

            // Act
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            // Assert: the notification is the last step of the save, so everything it writes is written by now.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });

            var savedContent = await File.ReadAllTextAsync(layoutFilePath);
            Assert.Contains("\"columnSpan\": 3", savedContent);
        }
        finally
        {
            cell.ColumnSpan = originalColumnSpan;
            await File.WriteAllTextAsync(layoutFilePath, originalContent);
        }
    }

    [Fact]
    public async Task WhenCellOfLayoutThatNoFileStoresIsSaved_ThenWarningIsShown()
    {
        // Arrange: the layout takes the place of the stored one in the tree, but the storage knows no file for it.
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var subjectFactory = _fixture.ServerServices.GetRequiredService<SubjectFactory>();
        var folder = GetDemoFolder(rootManager);
        var originalChildren = folder.Children;

        var cell = subjectFactory.CreateSubject<GridCell>();
        cell.Row = 0;
        cell.Column = 0;
        cell.Child = subjectFactory.CreateSubject<GridLayout>();

        var layout = subjectFactory.CreateSubject<GridLayout>();
        layout.Cells = [cell];

        try
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren) { ["Layout"] = layout };

            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var editMenu = page.Locator("[data-testid='edit-mode-menu']");
            await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editMenu.ClickAsync();
            await page.GetByText("Inline").ClickAsync();

            var editCellButton = page.GetByTitle("Edit Cell");
            await Assertions.Expect(editCellButton).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editCellButton.ClickAsync();

            var dialog = page.Locator(".mud-dialog");
            var columnSpanField = dialog.GetByLabel("Column Span");
            await Assertions.Expect(columnSpanField).ToBeVisibleAsync(new() { Timeout = 30000 });
            await columnSpanField.FillAsync("3");

            // Act
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            // Assert
            await Assertions.Expect(page.GetByText("Configuration applied but not saved"))
                .ToBeVisibleAsync(new() { Timeout = 30000 });
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToHaveCountAsync(0);
            Assert.Equal(3, cell.ColumnSpan);
        }
        finally
        {
            folder.Children = originalChildren;
        }
    }

    private static VirtualFolder GetDemoFolder(RootManager rootManager)
    {
        var root = (FluentStorageContainer)rootManager.Root!;
        return (VirtualFolder)root.Children["Demo"];
    }

    private static GridLayout GetLayoutSubject(RootManager rootManager)
        => (GridLayout)GetDemoFolder(rootManager).Children["Layout"];
}
