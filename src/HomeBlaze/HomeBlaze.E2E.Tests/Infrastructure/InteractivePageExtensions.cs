using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Waits for the circuit of a page. Pages are prerendered: until the circuit renders the page, a click on it is lost,
/// and the circuit replaces the prerendered elements, so a measurement of one of them can read a detached element.
/// </summary>
internal static class InteractivePageExtensions
{
    // The automatic time zone resolves through JS after the circuit has rendered, so the app bar menu showing it tells
    // that the circuit is connected.
    private const string ResolvedAutomaticTimeZone = "[data-testid='app-bar-menu'][data-time-zone^='Automatic (']";

    /// <summary>
    /// Waits until the page is rendered by its circuit.
    /// </summary>
    public static Task WaitUntilInteractiveAsync(this IPage page, float timeout = 30000)
        => Assertions.Expect(page.Locator(ResolvedAutomaticTimeZone)).ToBeVisibleAsync(new() { Timeout = timeout });
}
