using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>The box of an element in the viewport, in pixels.</summary>
internal sealed record Box(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;

    public double Bottom => Top + Height;
}

/// <summary>Where elements are on a page, and mouse gestures between such places.</summary>
internal static class PageGeometry
{
    // Left, top, width and height come from one call, so that a layout that is still moving cannot give values of different moments.
    public static async Task<Box> GetBoxAsync(ILocator locator)
    {
        var values = await locator.EvaluateAsync<double[]>(
            """
            element => {
                const rectangle = element.getBoundingClientRect();
                return [rectangle.left, rectangle.top, rectangle.width, rectangle.height];
            }
            """);

        return new Box(values[0], values[1], values[2], values[3]);
    }

    public static async Task DragAsync(IPage page, double fromX, double fromY, double toX, double toY)
    {
        await page.Mouse.MoveAsync((float)fromX, (float)fromY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)toX, (float)toY, new() { Steps = 10 });
        await page.Mouse.UpAsync();
    }
}
