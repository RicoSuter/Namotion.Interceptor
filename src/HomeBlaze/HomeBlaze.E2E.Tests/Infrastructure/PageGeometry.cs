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

    // All boxes come from one call for the same reason, so that content above that moves the page between two calls
    // cannot place one element against another where they never stood.
    public static async Task<Box[]> GetBoxesAsync(params ILocator[] locators)
    {
        var elements = new IElementHandle[locators.Length];
        try
        {
            for (var index = 0; index < locators.Length; index++)
            {
                elements[index] = await locators[index].ElementHandleAsync();
            }

            var values = await locators[0].Page.EvaluateAsync<double[][]>(
                """
                elements => elements.map(element => {
                    const rectangle = element.getBoundingClientRect();
                    return [rectangle.left, rectangle.top, rectangle.width, rectangle.height];
                })
                """,
                elements);

            return values.Select(value => new Box(value[0], value[1], value[2], value[3])).ToArray();
        }
        finally
        {
            foreach (var element in elements)
            {
                if (element is not null)
                {
                    await element.DisposeAsync();
                }
            }
        }
    }

    public static async Task DragAsync(IPage page, double fromX, double fromY, double toX, double toY)
    {
        await page.Mouse.MoveAsync((float)fromX, (float)fromY);
        await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync((float)toX, (float)toY, new() { Steps = 10 });
        await page.Mouse.UpAsync();
    }
}
