using System.Text.RegularExpressions;
using HomeBlaze.Abstractions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Host.Components.Browser;
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

    // Resolves once the value of a row lies below its name, or to its right on the same line when not stacked.
    private const string RowIsLaidOut = """
        ([row, isStacked]) => {
            // A row that left the page has empty boxes, which would pass for a stacked one.
            if (!row.isConnected) {
                return false;
            }

            const name = row.querySelector("[data-testid='subject-pane-value-name']").getBoundingClientRect();
            const value = row.querySelector("[data-testid='subject-pane-value-text']").getBoundingClientRect();
            return isStacked
                ? value.top >= name.bottom - 1 && Math.abs(value.left - name.left) <= 1
                : value.left >= name.right - 1 && value.top < name.bottom - 1;
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

    private async Task<IPage> OpenAsync(string path, int width, int height, int paneCount, BrowserNewContextOptions? contextOptions = null)
    {
        var page = await _fixture.CreatePageAsync(contextOptions);
        await page.SetViewportSizeAsync(width, height);
        await page.GotoAsync($"{_fixture.ServerAddress}{path}");
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = PageLoadTimeout });
        await WaitUntilSettledAsync(page, paneCount);
        return page;
    }

    private static Task WaitUntilSettledAsync(IPage page, int paneCount) =>
        page.WaitForFunctionAsync(PanesAreSettled, paneCount, new() { Timeout = PageLoadTimeout });

    private static ILocator Child(IPage page, string title) =>
        page.Locator("#scrollContainer .mud-list-item").GetByText(title, new() { Exact = true });

    private static ILocator LastPane(IPage page) =>
        page.Locator("#scrollContainer [data-testid='subject-pane']").Last;

    // Matches the whole text, since a filter by plain text would also find a category whose name only contains it.
    private static ILocator CategoryTitle(IPage page, string name) =>
        page.Locator("[data-testid='subject-pane-category-title']")
            .Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(name)}$") });

    private static ILocator Method(ILocator scope, string title) =>
        scope.GetByRole(AriaRole.Button, new() { Name = title, Exact = true });

    // The row of a value, by the whole name the pane shows for it.
    private static ILocator ValueRow(ILocator pane, string name) =>
        pane.Locator("[data-testid='subject-pane-value']").Filter(new()
        {
            Has = pane.Page.Locator("[data-testid='subject-pane-value-name']")
                .Filter(new() { HasTextRegex = new Regex($"^{Regex.Escape(name)}$") })
        });

    // The status of the motor always has a value, and one short enough to fit beside its name in a pane of a phone.
    private static async Task WaitForRowLayoutAsync(IPage page, bool isStacked)
    {
        var row = await ValueRow(LastPane(page), "Status").ElementHandleAsync(new() { Timeout = PageLoadTimeout });
        await page.WaitForFunctionAsync(RowIsLaidOut, new object[] { row, isStacked }, new() { Timeout = PageLoadTimeout });
    }

    // The menu passes its test id to its root, in which its button is the only one.
    private static ILocator RowLayoutMenuButton(IPage page) =>
        page.Locator("[data-testid='row-layout-menu']").GetByRole(AriaRole.Button);

    private static async Task ChooseRowLayoutAsync(IPage page, string item)
    {
        await RowLayoutMenuButton(page).ClickAsync();
        await page.Locator($"[data-testid='{item}']").ClickAsync();
    }

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
    [InlineData(true)]
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
        }
        finally
        {
            developerMode.SetEnabled(wasDeveloperMode);
        }
    }

    [Fact]
    public async Task WhenASubjectHasChildrenAndMethods_ThenTheMethodsPrecedeTheChildren()
    {
        // Act
        var page = await OpenAsync("browser", 1280, 720, paneCount: 1);

        // Assert
        var methodsPrecedeChildren = await page.EvaluateAsync<bool>("""
            () => {
                const pane = document.querySelector("#scrollContainer [data-testid='subject-pane']");
                const firstChild = pane.querySelector('.mud-list-item');
                const method = [...pane.querySelectorAll('button')].find(button => button.textContent.includes('Create'));
                return (method.compareDocumentPosition(firstChild) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
            }
            """);
        Assert.True(methodsPrecedeChildren);
    }

    [Fact]
    public async Task WhenAMethodHasACategory_ThenItsButtonIsInsideTheCategory()
    {
        // Act
        var page = await OpenAsync(MotorPath, 1280, 720, paneCount: 3);

        // Assert
        var pane = LastPane(page);
        var category = pane.Locator("[data-testid='subject-pane-category']").Filter(new() { Has = CategoryTitle(page, "Speed") });
        await Assertions.Expect(Method(category, "Set Speed")).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(Method(pane, "Set Speed")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task WhenAMethodHasNoCategory_ThenItsButtonPrecedesTheCategories()
    {
        // Act
        var page = await OpenAsync(MotorPath, 1280, 720, paneCount: 3);

        // Assert
        var pane = LastPane(page);
        var title = pane.Locator(CategoryTitle(page, "Speed"));
        await Assertions.Expect(title).ToBeVisibleAsync(new() { Timeout = PageLoadTimeout });

        var methodPrecedesCategory = await Method(pane, "Emergency Stop").EvaluateAsync<bool>(
            "(method, title) => (method.compareDocumentPosition(title) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0",
            await title.ElementHandleAsync());
        Assert.True(methodPrecedesCategory, "The button without a category should come before the heading of the category.");
    }

    [Fact]
    public async Task WhenAValueIsNull_ThenItIsListedInTheNoValueLineAndHasNoRow()
    {
        // Arrange
        var page = await OpenAsync("browser/Plugins", 1280, 720, paneCount: 2);

        // Act
        await Child(page, "MyCompany.SamplePlugin1.HomeBlaze v1.0.0").ClickAsync();

        // Assert
        await WaitUntilSettledAsync(page, paneCount: 3);

        // A plugin that was loaded has no status message.
        var pane = LastPane(page);
        var emptyValues = pane.Locator("[data-testid='subject-pane-empty-values']").Filter(new() { HasText = "StatusMessage" });
        await Assertions.Expect(emptyValues).ToContainTextAsync("No value:", new() { Timeout = PageLoadTimeout });

        // The status has a value and therefore a row, which shows that a row is found by its name.
        await Assertions.Expect(ValueRow(pane, "Status")).ToHaveCountAsync(1);
        await Assertions.Expect(ValueRow(pane, "StatusMessage")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAStatePathLeadsToASubject_ThenItsRowShowsTheSubjectAndLinksToIt()
    {
        // Arrange
        var resolver = _fixture.ServerServices.GetRequiredService<SubjectPathResolver>();
        var motor = resolver.ResolveSubject("/Demo/Motor", PathStyle.Route)
            ?? throw new InvalidOperationException("The demo motor is missing.");
        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            subject.LinkedPath = resolver.GetPath(motor, PathStyle.Canonical);
            var page = await OpenAsync($"browser/{name}", 1280, 720, paneCount: 2);
            var row = ValueRow(LastPane(page), "LinkedPath");
            var link = row.Locator("[data-testid='subject-pane-value-link']");
            await Assertions.Expect(row.Locator("[data-testid='subject-pane-value-text']"))
                .ToHaveTextAsync(((ITitleProvider)motor).Title!, new() { Timeout = PageLoadTimeout });
            await Assertions.Expect(link).ToHaveAttributeAsync("href", "/browser/Demo/Motor");

            // Act
            await link.ClickAsync();

            // Assert
            await page.WaitForURLAsync("**/browser/Demo/Motor", new() { Timeout = PageLoadTimeout });
            await WaitUntilSettledAsync(page, paneCount: 3);
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }

    [Theory]
    [InlineData("https://example.com/page", UriKind.Absolute, true)]
    [InlineData("javascript:alert(1)", UriKind.Absolute, false)]
    [InlineData("page", UriKind.Relative, false)]
    public async Task WhenAStateValueIsAUri_ThenOnlyAWebAddressGetsALink(string address, UriKind kind, bool isLink)
    {
        // Arrange
        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            subject.Website = new Uri(address, kind);

            // Act
            var page = await OpenAsync($"browser/{name}", 1280, 720, paneCount: 2);

            // Assert
            var row = ValueRow(LastPane(page), "Website");
            await Assertions.Expect(row.Locator("[data-testid='subject-pane-value-text']"))
                .ToHaveTextAsync(address, new() { Timeout = PageLoadTimeout });
            var link = row.Locator("[data-testid='subject-pane-value-link']");
            var copy = row.Locator("[data-testid='subject-pane-value-copy']");
            if (isLink)
            {
                await Assertions.Expect(link).ToHaveAttributeAsync("href", address);
                await Assertions.Expect(link).ToHaveAttributeAsync("target", "_blank");
                await Assertions.Expect(link).ToHaveAttributeAsync("rel", "noopener noreferrer");
                await Assertions.Expect(copy).ToHaveCountAsync(0);
            }
            else
            {
                await Assertions.Expect(link).ToHaveCountAsync(0);
                await Assertions.Expect(copy).ToHaveCountAsync(1);
            }
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // Developer mode replaces the content of a pane on every change of a value.
    public async Task WhenTheCopyButtonOfAChangedValueIsClicked_ThenTheValueIsInTheClipboard(bool isDeveloperMode)
    {
        // Arrange
        var developerMode = _fixture.ServerServices.GetRequiredService<DeveloperModeService>();
        var wasDeveloperMode = developerMode.IsEnabled;
        developerMode.SetEnabled(isDeveloperMode);
        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            // A path that leads to no subject is shown as text, with a copy button.
            subject.LinkedPath = "/Missing/First";
            var page = await OpenAsync($"browser/{name}", 1280, 720, paneCount: 2,
                new() { Permissions = ["clipboard-read", "clipboard-write"] });
            var row = ValueRow(LastPane(page), "LinkedPath");
            var copy = row.Locator("[data-testid='subject-pane-value-copy']");
            await Assertions.Expect(copy).ToHaveCSSAsync("opacity", "0", new() { Timeout = PageLoadTimeout });

            subject.LinkedPath = "/Missing/Second";
            await Assertions.Expect(row.Locator("[data-testid='subject-pane-value-text']"))
                .ToHaveTextAsync("/Missing/Second", new() { Timeout = PageLoadTimeout });

            // Act
            await row.HoverAsync();
            await Assertions.Expect(copy).ToHaveCSSAsync("opacity", "1");
            await copy.ClickAsync();

            // Assert
            await Assertions.Expect(page.Locator(".mud-snackbar").GetByText("Copied")).ToHaveCountAsync(1, new() { Timeout = PageLoadTimeout });
            Assert.Equal("/Missing/Second", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
            developerMode.SetEnabled(wasDeveloperMode);
        }
    }

    [Theory]
    [InlineData(1280, 720, false)]
    [InlineData(390, 844, true)] // A phone: narrower than the sm breakpoint of MudBlazor.
    public async Task WhenTheRowLayoutIsAutomatic_ThenAValueIsBelowItsNameOnlyOnAPhone(int width, int height, bool isStacked)
    {
        // Act
        var page = await OpenAsync(MotorPath, width, height, paneCount: 3);

        // Assert
        await WaitForRowLayoutAsync(page, isStacked);
    }

    [Theory]
    [InlineData(1280, 390)]
    [InlineData(390, 1280)]
    public async Task WhenTheViewIsResizedAcrossThePhoneWidth_ThenTheRowLayoutFollowsWithoutAReload(int widthBefore, int widthAfter)
    {
        // Arrange
        var page = await OpenAsync(MotorPath, widthBefore, 844, paneCount: 3);
        await WaitForRowLayoutAsync(page, isStacked: widthBefore < 600);

        // Act
        await page.SetViewportSizeAsync(widthAfter, 844);

        // Assert
        await WaitForRowLayoutAsync(page, isStacked: widthAfter < 600);
    }

    [Theory]
    [InlineData(1280, 720, "row-layout-stacked", true)]
    [InlineData(390, 844, "row-layout-side-by-side", false)]
    public async Task WhenARowLayoutIsChosen_ThenItOverridesTheWidthOfTheView(int width, int height, string item, bool isStacked)
    {
        // Arrange
        var page = await OpenAsync(MotorPath, width, height, paneCount: 3);
        await WaitForRowLayoutAsync(page, !isStacked);

        // Act
        await ChooseRowLayoutAsync(page, item);

        // Assert
        await WaitForRowLayoutAsync(page, isStacked);
    }

    [Theory]
    [InlineData(1280, 720, "row-layout-stacked", true)]
    [InlineData(390, 844, "row-layout-side-by-side", false)]
    public async Task WhenThePageIsReloaded_ThenTheChosenRowLayoutIsKept(int width, int height, string item, bool isStacked)
    {
        // Arrange
        var page = await OpenAsync(MotorPath, width, height, paneCount: 3);
        await ChooseRowLayoutAsync(page, item);
        await WaitForRowLayoutAsync(page, isStacked);
        await page.WaitForFunctionAsync(
            "name => document.cookie.includes(`${name}=`)",
            PropertyRowLayoutInitializer.CookieName,
            new() { Timeout = PageLoadTimeout });

        // Act
        await page.ReloadAsync();

        // Assert
        await page.WaitForFunctionAsync(SubjectBrowserPage.IsInteractive, null, new() { Timeout = PageLoadTimeout });
        await WaitUntilSettledAsync(page, paneCount: 3);
        await WaitForRowLayoutAsync(page, isStacked);
    }

    [Fact]
    public async Task WhenARowLayoutIsChosen_ThenTheMenuMarksIt()
    {
        // Arrange
        var page = await OpenAsync(MotorPath, 1280, 720, paneCount: 3);
        await ChooseRowLayoutAsync(page, "row-layout-stacked");
        await WaitForRowLayoutAsync(page, isStacked: true);

        // Act
        await RowLayoutMenuButton(page).ClickAsync();

        // Assert
        await Assertions.Expect(page.Locator("[data-testid='row-layout-stacked']"))
            .ToHaveAttributeAsync("aria-checked", "true", new() { Timeout = PageLoadTimeout });
        await Assertions.Expect(page.Locator("[data-testid='row-layout-automatic']")).ToHaveAttributeAsync("aria-checked", "false");
        await Assertions.Expect(page.Locator("[data-testid='row-layout-side-by-side']")).ToHaveAttributeAsync("aria-checked", "false");
    }
}
