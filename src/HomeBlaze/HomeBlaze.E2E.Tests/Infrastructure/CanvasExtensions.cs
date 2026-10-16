using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Waits for the nodes of a canvas layout.
/// </summary>
internal static class CanvasExtensions
{
    /// <summary>
    /// Waits until the library has measured every node of the canvas and drawn its two rectangles, its border and the
    /// area that a press acts on, and scrolls the first node into view. Before that, a press on a node neither selects
    /// nor moves it, nor starts a link.
    /// </summary>
    public static async Task WaitForCanvasNodesAsync(this ILocator canvas, int nodeCount, float timeout = 30000)
    {
        await Assertions.Expect(canvas.Locator("svg rect")).ToHaveCountAsync(2 * nodeCount, new() { Timeout = timeout });
        await canvas.Locator(".canvas-node").First.ScrollIntoViewIfNeededAsync();
    }
}
