using HomeBlaze.Components;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for saving a subject that is nested in the configuration of a subject stored as its own file.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class NestedSubjectSaveTests
{
    private const string LayoutPath = "Demo/Layout.json";
    private const string SavedColumnSpan = "\"columnSpan\": 3";

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
        var cell = ((GridLayout)_fixture.GetDemoFolder().Children["Layout"]).Cells.Single();
        var originalColumnSpan = cell.ColumnSpan;

        // The output copy outlives a run, so one that was stopped before its cleanup would leave the value behind.
        Assert.DoesNotContain(SavedColumnSpan, originalContent);

        try
        {
            var page = await _fixture.OpenLayoutPageInInlineModeAsync();

            // Act
            await SaveCellWithColumnSpanThreeAsync(page);

            // Assert: the notification is the last step of the save, so everything it writes is written by now.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.Contains(SavedColumnSpan, await File.ReadAllTextAsync(layoutFilePath));
        }
        finally
        {
            cell.ColumnSpan = originalColumnSpan;
            await File.WriteAllTextAsync(layoutFilePath, originalContent);
        }
    }

    [Fact]
    public async Task WhenCellOfUnstoredLayoutIsSaved_ThenWarningIsShown()
    {
        // Arrange
        var (layout, cell) = _fixture.CreateGridWithCell();

        await _fixture.WithUnstoredLayoutAsync(layout, async page =>
        {
            // Act
            await SaveCellWithColumnSpanThreeAsync(page);

            // Assert
            await Assertions.Expect(page.GetByText("Configuration applied but not saved"))
                .ToBeVisibleAsync(new() { Timeout = 30000 });
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToHaveCountAsync(0);
            Assert.Equal(3, cell.ColumnSpan);
        });
    }

    private static async Task SaveCellWithColumnSpanThreeAsync(IPage page)
    {
        var editCellButton = page.GetByTitle("Edit Cell");
        await Assertions.Expect(editCellButton).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editCellButton.ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        var columnSpanField = dialog.GetByLabel("Column Span");
        await Assertions.Expect(columnSpanField).ToBeVisibleAsync(new() { Timeout = 30000 });
        await columnSpanField.FillAsync("3");

        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
    }
}
