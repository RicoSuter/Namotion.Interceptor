using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Waits for the nodes of a canvas layout, and moves them.
/// </summary>
internal static class CanvasExtensions
{
    // How long the pointer rests on a node for the library to show it as hovered, before it enters the node again.
    private const float HoverAttemptTimeout = 2000;

    // The library gives the border of the node under the pointer this stroke (Excubo.Blazor.Diagrams 4.1.136).
    private const string HoveredBorderScript =
        """
        ([x, y]) => [...document.querySelectorAll('.canvas-layout-container svg rect[cursor=pointer][stroke="#DDDDDD7F"]')]
            .some(border => {
                const area = border.getBoundingClientRect();
                return x >= area.left && x <= area.right && y >= area.top && y <= area.bottom;
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
    /// hovered. The drag starts at the given fractions of the width and the height of the node, at its center by default.
    /// </summary>
    /// <exception cref="TimeoutException">The library did not show the node as hovered within the timeout.</exception>
    public static async Task DragNodeAsync(
        this ILocator node, double deltaX, double deltaY, double startX = 0.5, double startY = 0.5, float timeout = 30000)
    {
        var page = node.Page;
        await node.ScrollIntoViewIfNeededAsync();

        // The library moves a node only when it handled the pointer entering the node before the press. A press it
        // handles before that is on the empty canvas and pans it.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeout);
        while (true)
        {
            var box = await PageGeometry.GetBoxAsync(node);
            var x = box.Left + box.Width * startX;
            var y = box.Top + box.Height * startY;

            // An entry the library missed is not reported again while the pointer stays on the node, so it leaves first.
            await page.Mouse.MoveAsync(0, 0);
            await page.Mouse.MoveAsync((float)x, (float)y);
            try
            {
                await page.WaitForFunctionAsync(HoveredBorderScript, new[] { x, y }, new() { Timeout = HoverAttemptTimeout });
            }
            catch (TimeoutException exception) when (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The canvas did not show the node under the pointer as hovered", exception);
            }
            catch (TimeoutException)
            {
                continue;
            }

            await PageGeometry.DragAsync(page, x, y, x + deltaX, y + deltaY);
            return;
        }
    }
}
