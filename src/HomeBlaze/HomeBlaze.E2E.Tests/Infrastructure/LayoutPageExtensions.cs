using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Namotion.Interceptor;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// The page of the test data that shows the layout stored as its own file, for tests that edit that layout.
/// </summary>
internal static class LayoutPageExtensions
{
    private const string PagePath = "Demo/LayoutPage.md";

    /// <summary>
    /// Opens the layout page in inline edit mode.
    /// </summary>
    public static Task<IPage> OpenLayoutPageInInlineModeAsync(this PlaywrightFixture fixture)
        => fixture.OpenPageInEditModeAsync(PagePath);

    /// <summary>
    /// Opens a page, given by its path under the test data folder, in an edit mode of the edit mode menu.
    /// </summary>
    public static async Task<IPage> OpenPageInEditModeAsync(this PlaywrightFixture fixture, string pagePath, string editMode = "Inline")
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync($"{fixture.ServerAddress}pages/{pagePath}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync();

        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.Locator(".mud-popover-open").GetByText(editMode, new() { Exact = true }).ClickAsync();

        return page;
    }

    /// <summary>
    /// Runs the test on the layout page in inline edit mode, with the layout in the place of the stored one
    /// in the tree, so that the storage knows no file for it.
    /// </summary>
    public static async Task WithUnstoredLayoutAsync(
        this PlaywrightFixture fixture, IInterceptorSubject layout, Func<IPage, Task> testAsync)
    {
        // A pass of the storage that finds a change sets the children of the folder anew from its files, which drops the
        // layout. A pass now takes in what earlier tests left changed on disk, so that the pass they asked for finds nothing.
        var root = (FluentStorageContainer)fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        await root.ReconcileAsync();

        var folder = fixture.GetDemoFolder();
        var originalChildren = folder.Children;

        try
        {
            folder.Children = new Dictionary<string, IInterceptorSubject>(originalChildren) { ["Layout"] = layout };
            await testAsync(await fixture.OpenLayoutPageInInlineModeAsync());
        }
        finally
        {
            folder.Children = originalChildren;
        }
    }

    /// <summary>
    /// Creates a grid with one cell that holds an empty grid, so that the cell shows its buttons.
    /// </summary>
    public static (GridLayout Layout, GridCell Cell) CreateGridWithCell(this PlaywrightFixture fixture)
    {
        var subjectFactory = fixture.ServerServices.GetRequiredService<SubjectFactory>();

        var cell = subjectFactory.CreateSubject<GridCell>();
        cell.Row = 0;
        cell.Column = 0;
        cell.Child = subjectFactory.CreateSubject<GridLayout>();

        var layout = subjectFactory.CreateSubject<GridLayout>();
        layout.Cells = [cell];

        return (layout, cell);
    }

    public static VirtualFolder GetDemoFolder(this PlaywrightFixture fixture)
    {
        var root = (FluentStorageContainer)fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        return (VirtualFolder)root.Children["Demo"];
    }
}
