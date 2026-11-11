using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Scripts and helpers for the subject browser page.
/// </summary>
public static class SubjectBrowserPage
{
    private const int PageLoadTimeout = 30000;

    /// <summary>
    /// Resolves once the browser is interactive. The page is prerendered, and a click before its circuit is
    /// interactive is lost. The pane width is set by the script the interactive browser runs, so it marks the point
    /// from which clicks arrive.
    /// </summary>
    public const string IsInteractive =
        "() => (document.getElementById('scrollContainer')?.style.getPropertyValue('--pane-width') ?? '') !== ''";

    /// <summary>Opens the subject at <paramref name="path"/> in a new page of the browser and waits until it is interactive.</summary>
    public static async Task<IPage> OpenAsync(PlaywrightFixture fixture, string path)
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync($"{fixture.ServerAddress}browser/{path}");
        await page.WaitForFunctionAsync(IsInteractive, null, new() { Timeout = PageLoadTimeout });
        return page;
    }

    /// <summary>Gets the button of the operation or query titled <paramref name="title"/> in the pane.</summary>
    public static ILocator Method(ILocator pane, string title) =>
        pane.Locator(".subject-pane-method").Filter(new() { HasTextString = title });

    /// <summary>
    /// Clicks the operation titled <paramref name="title"/> in the pane and returns its parameter dialog, in which the text
    /// inputs and then the JSON fields follow the order of the parameters.
    /// </summary>
    public static async Task<ILocator> OpenOperationDialogAsync(ILocator pane, string title)
    {
        var operation = Method(pane, title);
        await Assertions.Expect(operation).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });
        await operation.ClickAsync();

        var dialog = pane.Page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog).ToContainTextAsync(title, new() { Timeout = PageLoadTimeout });
        await dialog.WaitForDialogFocusAsync();
        return dialog;
    }

    /// <summary>Clicks "Execute" in the parameter dialog of an operation.</summary>
    public static async Task ExecuteAsync(ILocator dialog)
    {
        var execute = dialog.GetByRole(AriaRole.Button, new() { Name = "Execute", Exact = true });
        await Assertions.Expect(execute).ToBeEnabledAsync(new() { Timeout = PageLoadTimeout });
        await execute.ClickAsync();
    }
}
