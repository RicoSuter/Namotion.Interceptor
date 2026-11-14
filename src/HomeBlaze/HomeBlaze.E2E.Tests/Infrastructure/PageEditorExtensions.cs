using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Opens pages of the demo folder in an edit mode and follows the source editor of the Split mode and the setup dialog of a
/// new subject.
/// </summary>
internal static class PageEditorExtensions
{
    private const int Timeout = 30000;

    /// <summary>
    /// The options of the browser context of a phone, which has a touch screen.
    /// </summary>
    public static BrowserNewContextOptions PhoneContextOptions =>
        new() { HasTouch = true, IsMobile = true, ViewportSize = new() { Width = 375, Height = 667 } };

    /// <summary>
    /// Writes a page of the demo folder, given by its file name, and opens it in inline edit mode.
    /// </summary>
    public static async Task<IPage> OpenDemoPageInInlineModeAsync(
        this PlaywrightFixture fixture, string pageName, string content, BrowserNewContextOptions? contextOptions = null)
    {
        await fixture.WriteDemoPageAsync(pageName, content);
        var page = await fixture.CreatePageAsync(contextOptions);
        await page.GotoAsync($"{fixture.ServerAddress}pages/Demo/{pageName}");

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        return page;
    }

    /// <summary>
    /// Gets the 1-based line of the content that contains the text.
    /// </summary>
    public static int GetLineOf(string content, string text) => content.Split('\n').ToList().FindIndex(line => line.Contains(text)) + 1;

    /// <summary>
    /// Waits until the source editor of the page selects from the 1-based line. The source editor must be the only Monaco
    /// editor, as it is while no text dialog is open.
    /// </summary>
    public static Task WaitForPageEditorSelectionAsync(this IPage page, int startLine) =>
        page.WaitForFunctionAsync(
            "line => monaco.editor.getEditors()[0]?.getSelection()?.startLineNumber === line",
            startLine,
            new() { Timeout = Timeout });

    /// <summary>
    /// Chooses an alert with the name in the open setup dialog of a new subject and creates it.
    /// </summary>
    public static async Task CreateAlertAsync(this IPage page, string name)
    {
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByTestId("type-search-input").FillAsync("Alert");
        await dialog.GetByTestId("type-row-alert").ClickAsync();

        // The test id of the name field is on its input, and the actions of the dialog stand outside the element with its test id.
        await dialog.GetByTestId("create-name-input").FillAsync(name);
        await page.GetByTestId("create-button").ClickAsync();
    }
}
