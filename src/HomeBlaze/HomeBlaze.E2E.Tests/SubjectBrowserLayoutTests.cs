using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the layout of the subject browser: its panes are never cut off at an edge of the view, and the
/// pane a path leads to is the one in view.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class SubjectBrowserLayoutTests
{
    private const int PageLoadTimeout = 30000;

    // Root, Demo and the motor: three panes, which is more than the narrow views below show at once.
    private const string MotorPath = "browser/Demo/Motor";

    // Takes the number of panes the row should have. Resolves once it has them and no placeholder, the last pane lies
    // inside the visible part of the row, and snapping is back on, which it is not while the row changes or scrolls.
    private const string PanesAreSettled = """
        count => {
            const container = document.getElementById('scrollContainer');
            const panes = container ? [...container.querySelectorAll("[data-testid='subject-pane']")] : [];
            if (panes.length !== count
                || container.querySelector("[data-testid='subject-pane-placeholder']") !== null
                || getComputedStyle(container).scrollSnapType !== 'x mandatory') {
                return false;
            }

            const view = container.getBoundingClientRect();
            const last = panes.at(-1).getBoundingClientRect();
            return last.left >= view.left - 1 && last.right <= view.left + container.clientWidth + 1;
        }
        """;

    // Counts the panes that are only partly inside the visible part of the pane row.
    private const string CountPartlyVisiblePanes = """
        () => {
            const container = document.getElementById('scrollContainer');
            const view = container.getBoundingClientRect();
            const viewRight = view.left + container.clientWidth;
            return [...container.querySelectorAll("[data-testid='subject-pane']")]
                .map(pane => pane.getBoundingClientRect())
                .filter(pane => pane.right > view.left + 1 && pane.left < viewRight - 1)
                .filter(pane => pane.left < view.left - 1 || pane.right > viewRight + 1)
                .length;
        }
        """;

    // The page is prerendered, and a click before its circuit is interactive is lost. The pane width is set by the
    // script the interactive browser runs, so it marks the point from which clicks arrive.
    private const string BrowserIsInteractive =
        "() => (document.getElementById('scrollContainer')?.style.getPropertyValue('--pane-width') ?? '') !== ''";

    // Records where the row is when placeholders appear, the positions it scrolls through, and where its scrolling had
    // got to when they are removed. The position at removal comes from the scroll events: once the placeholders are
    // gone the row is shorter, and reading it then says nothing about where it was.
    private const string WatchPlaceholders = """
        () => {
            const container = document.getElementById('scrollContainer');
            const countPlaceholders = () => container.querySelectorAll("[data-testid='subject-pane-placeholder']").length;
            const watch = { start: container.scrollLeft, appearedAt: null, removedAt: null, most: 0, positions: [] };
            let scrolledTo = container.scrollLeft;
            container.addEventListener('scroll', () => {
                scrolledTo = container.scrollLeft;
                watch.positions.push(scrolledTo);
            });
            new MutationObserver(() => {
                const count = countPlaceholders();
                if (count > 0 && watch.appearedAt === null) {
                    watch.appearedAt = container.scrollLeft;
                }

                if (count === 0 && watch.most > 0 && watch.removedAt === null) {
                    watch.removedAt = scrolledTo;
                }

                watch.most = Math.max(watch.most, count);
            }).observe(container, { childList: true });
            window.placeholderWatch = watch;
        }
        """;

    private readonly PlaywrightFixture _fixture;

    public SubjectBrowserLayoutTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record PlaceholderWatch(double Start, double? AppearedAt, double? RemovedAt, int Most, double[] Positions);

    private static async Task<PlaceholderWatch> ReadPlaceholderWatchAsync(IPage page)
    {
        var values = await page.EvaluateAsync<double?[]>(
            "() => [window.placeholderWatch.start, window.placeholderWatch.appearedAt, window.placeholderWatch.removedAt, window.placeholderWatch.most]");
        var positions = await page.EvaluateAsync<double[]>("() => window.placeholderWatch.positions");
        return new PlaceholderWatch(values[0] ?? double.NaN, values[1], values[2], (int)(values[3] ?? 0), positions);
    }

    // A row that jumps reports only its new position; one that scrolls reports positions on the way.
    private static void AssertScrolledThrough(PlaceholderWatch watch, double end) =>
        Assert.Contains(watch.Positions, position => position > end + 1 && position < watch.Start - 1);

    private async Task<IPage> OpenAsync(string path, int width, int height, int paneCount)
    {
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(width, height);
        await page.GotoAsync($"{_fixture.ServerAddress}{path}");
        await page.WaitForFunctionAsync(BrowserIsInteractive, null, new() { Timeout = PageLoadTimeout });
        await WaitUntilSettledAsync(page, paneCount);
        return page;
    }

    private static Task WaitUntilSettledAsync(IPage page, int paneCount) =>
        page.WaitForFunctionAsync(PanesAreSettled, paneCount, new() { Timeout = PageLoadTimeout });

    private static ILocator Child(IPage page, string title) =>
        page.Locator("#scrollContainer .mud-list-item").GetByText(title, new() { Exact = true });

    [Fact]
    public async Task WhenADeepPathIsOpened_ThenItsPaneIsInViewAndNoPaneIsCutOff()
    {
        // Act
        var page = await OpenAsync(MotorPath, 800, 700, paneCount: 3);

        // Assert
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenAChildIsOpened_ThenItsPaneIsScrolledIntoViewAndNoPaneIsCutOff()
    {
        // Arrange
        var page = await OpenAsync("browser/Demo", 800, 700, paneCount: 2);

        // Act
        await Child(page, "Test Motor").ClickAsync();

        // Assert
        await WaitUntilSettledAsync(page, paneCount: 3);
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenAChildOfAMiddlePaneIsOpened_ThenItReplacesTheDeeperPanes()
    {
        // Arrange
        var page = await OpenAsync(MotorPath, 800, 700, paneCount: 3);

        // Act
        await Child(page, "Grid Demo").ClickAsync();

        // Assert
        await Assertions.Expect(page.Locator("#scrollContainer [data-testid='subject-pane']").Last.Locator("h1"))
            .ToContainTextAsync("Grid Demo", new() { Timeout = PageLoadTimeout });
        await WaitUntilSettledAsync(page, paneCount: 3);
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Theory]
    [InlineData(800, 700)] // Two panes in view: the closed pane is one of them.
    [InlineData(375, 812)] // One pane in view: the closed pane is the one the row rests on.
    public async Task WhenAPaneIsClosed_ThenAPlaceholderKeepsItsPlaceUntilTheRowHasScrolledBack(int width, int height)
    {
        // Arrange
        var page = await OpenAsync(MotorPath, width, height, paneCount: 3);
        await page.EvaluateAsync(WatchPlaceholders);

        // Act
        await page.Locator("#scrollContainer [data-testid='subject-pane']").Last
            .Locator("[data-testid='close-pane-button']").ClickAsync();

        // Assert
        await WaitUntilSettledAsync(page, paneCount: 2);
        var watch = await ReadPlaceholderWatchAsync(page);
        var end = await page.EvaluateAsync<double>("() => document.getElementById('scrollContainer').scrollLeft");
        Assert.Equal(1, watch.Most);
        Assert.Equal(watch.Start, watch.AppearedAt);
        Assert.True(end < watch.Start, "The row should have scrolled back.");
        AssertScrolledThrough(watch, end);
        Assert.InRange(watch.RemovedAt ?? double.NaN, end - 1, end + 1);
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenABreadcrumbLeadsBackSeveralLevels_ThenPlaceholdersKeepThePlacesUntilTheRowHasScrolledBack()
    {
        // Arrange
        var page = await OpenAsync(MotorPath, 375, 812, paneCount: 3);
        await page.EvaluateAsync(WatchPlaceholders);

        // Act
        await page.Locator(".browser-toolbar").GetByRole(AriaRole.Link, new() { Name = "Root", Exact = true }).ClickAsync();

        // Assert
        await WaitUntilSettledAsync(page, paneCount: 1);
        var watch = await ReadPlaceholderWatchAsync(page);
        Assert.Equal(2, watch.Most);
        Assert.Equal(watch.Start, watch.AppearedAt);
        AssertScrolledThrough(watch, end: 0);
        Assert.InRange(watch.RemovedAt ?? double.NaN, -1, 1);
    }

    [Fact]
    public async Task WhenTheViewIsNarrowerThanAPane_ThenAPaneIsAsWideAsTheView()
    {
        // Act
        var page = await OpenAsync(MotorPath, 375, 812, paneCount: 3);

        // Assert
        var widthDifference = await page.EvaluateAsync<double>("""
            () => {
                const container = document.getElementById('scrollContainer');
                const pane = container.querySelector("[data-testid='subject-pane']");
                return Math.abs(pane.getBoundingClientRect().width - container.clientWidth);
            }
            """);
        Assert.InRange(widthDifference, 0, 1);
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenTheViewIsResized_ThenThePanesAreFittedAgain()
    {
        // Arrange
        const string paneWidth = "() => document.getElementById('scrollContainer').style.getPropertyValue('--pane-width')";
        var page = await OpenAsync(MotorPath, 1280, 700, paneCount: 3);
        var widthBefore = await page.EvaluateAsync<string>(paneWidth);

        // Act
        await page.SetViewportSizeAsync(800, 700);

        // Assert
        await page.WaitForFunctionAsync(
            $"before => ({paneWidth})() !== before && ({CountPartlyVisiblePanes})() === 0",
            widthBefore,
            new() { Timeout = PageLoadTimeout });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // Developer mode puts an element of the tracking scope around the content of a pane.
    public async Task WhenAPaneHasMoreContentThanFits_ThenTheContentScrollsInsideThePane(bool isDeveloperMode)
    {
        // Arrange
        var developerMode = _fixture.ServerServices.GetRequiredService<DeveloperModeService>();
        var wasDeveloperMode = developerMode.IsEnabled;
        developerMode.SetEnabled(isDeveloperMode);
        try
        {
            // Act
            var page = await OpenAsync("browser", 800, 400, paneCount: 1);

            // Assert
            var isBounded = await page.EvaluateAsync<bool>("""
                () => {
                    const pane = document.querySelector("#scrollContainer [data-testid='subject-pane']");
                    const content = pane.querySelector("[data-testid='subject-pane-content']");
                    return content.scrollHeight > content.clientHeight
                        && content.getBoundingClientRect().bottom <= pane.firstElementChild.getBoundingClientRect().bottom + 1;
                }
                """);
            Assert.True(isBounded, "The content of the root pane should be taller than the pane and end inside it.");

            // The element developer mode adds is the one with the tooltip, and it has to be between pane and content.
            var wrapperCount = await page.Locator(
                "#scrollContainer [data-testid='subject-pane'] div[title]:has([data-testid='subject-pane-content'])").CountAsync();
            Assert.Equal(isDeveloperMode ? 1 : 0, wrapperCount);
        }
        finally
        {
            developerMode.SetEnabled(wasDeveloperMode);
        }
    }

    [Fact]
    public async Task WhenASubjectHasChildrenAndMethods_ThenTheMethodsFollowTheChildren()
    {
        // Act
        var page = await OpenAsync("browser", 1280, 720, paneCount: 1);

        // Assert
        var methodsFollowChildren = await page.EvaluateAsync<bool>("""
            () => {
                const pane = document.querySelector("#scrollContainer [data-testid='subject-pane']");
                const lastChild = [...pane.querySelectorAll('.mud-list-item')].at(-1);
                const method = [...pane.querySelectorAll('button')].find(button => button.textContent.includes('Create'));
                return (lastChild.compareDocumentPosition(method) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
            }
            """);
        Assert.True(methodsFollowChildren);
    }
}
