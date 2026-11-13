using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Waits for the nodes of a canvas layout, and moves them.
/// </summary>
internal static class CanvasExtensions
{
    // Bounds each wait of an attempt: for no node to be shown as hovered, and for the entered node to be.
    private const float HoverAttemptTimeout = 2000;

    // The library gives the border of the node under the pointer this stroke (Excubo.Blazor.Diagrams 4.1.136).
    private const string HoveredBorderSelector = """.canvas-layout-container svg rect[cursor=pointer][stroke="#DDDDDD7F"]""";

    // A pixel of slack, because a press at the edge of the node can fall a fraction of a pixel outside its border.
    private const string HoveredBorderAtScript =
        """
        ([selector, x, y]) => [...document.querySelectorAll(selector)].some(border => {
            const area = border.getBoundingClientRect();
            return x >= area.left - 1 && x <= area.right + 1 && y >= area.top - 1 && y <= area.bottom + 1;
        })
        """;

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

    /// <summary>
    /// Drags a node of an edited canvas by the given distance, once the library shows the node under the pointer as
    /// hovered. The drag starts at the center of the node, or at the middle of its left edge.
    /// </summary>
    /// <exception cref="TimeoutException">The library did not show the node as hovered within the timeout.</exception>
    public static async Task DragNodeAsync(
        this ILocator node, double deltaX, double deltaY, bool fromLeftEdge = false, float timeout = 30000)
    {
        var page = node.Page;
        await node.ScrollIntoViewIfNeededAsync(new() { Timeout = timeout });

        // The library moves a node only when it handled the pointer entering the node before the press. A press it
        // handles before that is on the empty canvas and pans it.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        double x;
        double y;
        while (true)
        {
            var box = await PageGeometry.GetBoxAsync(node);
            x = fromLeftEdge ? box.Left : box.Left + box.Width / 2;
            y = box.Top + box.Height / 2;

            // An entry the library missed is not reported again while the pointer stays on the node, so it leaves first,
            // and a node still shown as hovered from before must not pass for the new entry.
            await page.Mouse.MoveAsync(0, 0);
            try
            {
                await page.WaitForFunctionAsync(
                    "selector => document.querySelector(selector) === null", HoveredBorderSelector, new() { Timeout = HoverAttemptTimeout });
                await page.Mouse.MoveAsync((float)x, (float)y);
                await page.WaitForFunctionAsync(
                    HoveredBorderAtScript, new object[] { HoveredBorderSelector, x, y }, new() { Timeout = HoverAttemptTimeout });
                break;
            }
            catch (TimeoutException exception) when (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The canvas did not show the node under the pointer as hovered", exception);
            }
            catch (TimeoutException)
            {
            }
        }

        await PageGeometry.DragAsync(page, x, y, x + deltaX, y + deltaY);
    }
}
