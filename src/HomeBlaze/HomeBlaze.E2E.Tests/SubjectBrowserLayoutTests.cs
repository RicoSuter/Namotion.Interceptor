using HomeBlaze.E2E.Tests.Infrastructure;
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

    // Resolves once the last pane lies inside the visible part of the pane row.
    private const string LastPaneIsInView = """
        () => {
            const container = document.getElementById('scrollContainer');
            const panes = container ? [...container.querySelectorAll("[data-testid='subject-pane']")] : [];
            if (panes.length < 3) {
                return false;
            }

            const view = container.getBoundingClientRect();
            const last = panes.at(-1).getBoundingClientRect();
            return last.left >= view.left - 1 && last.right <= view.left + container.clientWidth + 1;
        }
        """;

    // Returns the panes that are only partly inside the visible part of the pane row.
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
        "() => document.getElementById('scrollContainer')?.style.getPropertyValue('--pane-width') !== ''";

    private readonly PlaywrightFixture _fixture;

    public SubjectBrowserLayoutTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenADeepPathIsOpened_ThenItsPaneIsInViewAndNoPaneIsCutOff()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(800, 700);

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}{MotorPath}");

        // Assert
        await page.WaitForFunctionAsync(LastPaneIsInView, null, new() { Timeout = PageLoadTimeout });
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenAChildIsOpened_ThenItsPaneIsScrolledIntoViewAndNoPaneIsCutOff()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(800, 700);
        await page.GotoAsync($"{_fixture.ServerAddress}browser/Demo");
        await page.WaitForFunctionAsync(BrowserIsInteractive, null, new() { Timeout = PageLoadTimeout });
        var motor = page.Locator("#scrollContainer .mud-list-item:has-text('Test Motor')").First;
        await Assertions.Expect(motor).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

        // Act
        await motor.ClickAsync();

        // Assert
        await page.WaitForFunctionAsync(LastPaneIsInView, null, new() { Timeout = PageLoadTimeout });
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenAPaneIsClosed_ThenAPlaceholderKeepsItsPlaceUntilTheRowHasScrolledBack()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(800, 700);
        await page.GotoAsync($"{_fixture.ServerAddress}{MotorPath}");
        await page.WaitForFunctionAsync(LastPaneIsInView, null, new() { Timeout = PageLoadTimeout });
        await page.EvaluateAsync("""
            () => {
                const container = document.getElementById('scrollContainer');
                const hasPlaceholder = () => container.querySelector("[data-testid='subject-pane-placeholder']") !== null;
                window.sawPanePlaceholder = false;
                new MutationObserver(() => window.sawPanePlaceholder ||= hasPlaceholder())
                    .observe(container, { childList: true });
            }
            """);

        // Act
        await page.Locator("#scrollContainer [data-testid='subject-pane']").Last.Locator("button.mud-fab").ClickAsync();

        // Assert
        await page.WaitForFunctionAsync("""
            () => {
                const container = document.getElementById('scrollContainer');
                return container.querySelectorAll("[data-testid='subject-pane']").length === 2
                    && container.querySelector("[data-testid='subject-pane-placeholder']") === null
                    && container.scrollLeft === 0;
            }
            """, null, new() { Timeout = PageLoadTimeout });
        Assert.True(await page.EvaluateAsync<bool>("() => window.sawPanePlaceholder"));
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenTheOnlyPaneInViewIsClosed_ThenTheRowScrollsBackInsteadOfJumping()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(375, 812);
        await page.GotoAsync($"{_fixture.ServerAddress}{MotorPath}");
        await page.WaitForFunctionAsync(LastPaneIsInView, null, new() { Timeout = PageLoadTimeout });
        await page.EvaluateAsync("""
            () => {
                const container = document.getElementById('scrollContainer');
                window.scrollStart = container.scrollLeft;
                window.scrollPositions = [];
                container.addEventListener('scroll', () => window.scrollPositions.push(container.scrollLeft));
            }
            """);

        // Act
        await page.Locator("#scrollContainer [data-testid='subject-pane']").Last.Locator("button.mud-fab").ClickAsync();

        // Assert
        await page.WaitForFunctionAsync("""
            () => {
                const container = document.getElementById('scrollContainer');
                return container.querySelectorAll("[data-testid='subject-pane']").length === 2
                    && container.querySelector("[data-testid='subject-pane-placeholder']") === null;
            }
            """, null, new() { Timeout = PageLoadTimeout });
        var positionsOnTheWay = await page.EvaluateAsync<int>("""
            () => {
                const end = document.getElementById('scrollContainer').scrollLeft;
                return window.scrollPositions.filter(position => position > end + 1 && position < window.scrollStart - 1).length;
            }
            """);
        Assert.True(positionsOnTheWay > 0, "The row jumped to the previous pane instead of scrolling to it.");
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenTheViewIsNarrowerThanAPane_ThenAPaneIsAsWideAsTheView()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(375, 812);

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}{MotorPath}");

        // Assert
        await page.WaitForFunctionAsync(LastPaneIsInView, null, new() { Timeout = PageLoadTimeout });
        Assert.Equal(0, await page.EvaluateAsync<int>(CountPartlyVisiblePanes));
    }

    [Fact]
    public async Task WhenASubjectHasChildrenAndMethods_ThenTheMethodsFollowTheChildren()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act
        await page.GotoAsync($"{_fixture.ServerAddress}browser");
        var createButton = page.Locator("#scrollContainer button:has-text('Create')").First;
        await Assertions.Expect(createButton).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

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
