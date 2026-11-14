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
}
