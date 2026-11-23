using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for Mermaid diagrams on markdown pages.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MermaidDiagramTests
{
    private const int Timeout = 30000;
    private const int ValidDiagram = 0;
    private const int InvalidDiagram = 1;
    private const int ActiveContentDiagram = 2;
    private const int SplitFunctionDiagram = 3;

    private readonly PlaywrightFixture _fixture;

    public MermaidDiagramTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync(Func<IPage, Task>? setUp = null)
    {
        var page = await _fixture.CreatePageAsync();
        if (setUp is not null)
        {
            await setUp(page);
        }

        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Diagrams.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        return page;
    }

    [Fact]
    public async Task WhenPageHasDiagram_ThenItIsShownAsSvg()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var diagram = page.Locator("pre.mermaid").Nth(ValidDiagram);

        // Assert
        await Assertions.Expect(diagram).ToHaveAttributeAsync("data-mermaid-state", "done", new() { Timeout = Timeout });
        await Assertions.Expect(diagram.Locator("svg")).ToBeVisibleAsync();
        await Assertions.Expect(diagram).ToContainTextAsync("Tank");
        Assert.Equal("function", await page.EvaluateAsync<string>("() => typeof window.define"));
    }

    [Fact]
    public async Task WhenDiagramIsInvalid_ThenItsSourceIsShown()
    {
        // Arrange & Act
        var page = await OpenPageAsync();
        var diagram = page.Locator("pre.mermaid").Nth(InvalidDiagram);

        // Assert
        await Assertions.Expect(diagram).ToHaveAttributeAsync("data-mermaid-state", "error", new() { Timeout = Timeout });
        await Assertions.Expect(diagram).ToBeVisibleAsync();
        await Assertions.Expect(diagram).ToHaveTextAsync("this is not a diagram");
        await Assertions.Expect(diagram.Locator("svg")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
    }

    [Fact]
    public async Task WhenDiagramContainsActiveContent_ThenNothingActiveIsRenderedOrLoaded()
    {
        // Arrange
        var isRequested = false;

        // Act
        var page = await OpenPageAsync(openingPage => openingPage.RouteAsync("**/mermaid-exfiltration.test/**", route =>
        {
            isRequested = true;
            return route.AbortAsync();
        }));
        var diagram = page.Locator("pre.mermaid").Nth(ActiveContentDiagram);
        await Assertions.Expect(diagram).ToHaveAttributeAsync("data-mermaid-state", "done", new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(diagram.Locator("img")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
        await Assertions.Expect(diagram.Locator("a")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
        await Assertions.Expect(diagram.Locator("foreignObject")).ToHaveCountAsync(0, new() { Timeout = AssertionTimeouts.Absence });
        var hasEventHandler = await diagram.EvaluateAsync<bool>(
            "element => [...element.querySelectorAll('*')].some(child => [...child.attributes].some(attribute => attribute.name.startsWith('on')))");
        Assert.False(hasEventHandler);
        Assert.False(isRequested);
    }

    [Fact]
    public async Task WhenDiagramSplitsACssFunctionName_ThenItsAddressIsNotLoaded()
    {
        // Arrange
        var isControlRequested = false;
        var isRequested = false;

        // Act
        var page = await OpenPageAsync(async openingPage =>
        {
            await openingPage.RouteAsync("**/mermaid-control.test/**", route =>
            {
                isControlRequested = true;
                return route.AbortAsync();
            });
            await openingPage.RouteAsync("**/mermaid-exfiltration.test/**", route =>
            {
                isRequested = true;
                return route.AbortAsync();
            });
        });
        var diagram = page.Locator("pre.mermaid").Nth(SplitFunctionDiagram);
        await Assertions.Expect(diagram).ToHaveAttributeAsync("data-mermaid-state", "done", new() { Timeout = Timeout });

        // The joined url( of the same diagram loads, so the split ones are applied by then and would have loaded too.
        await AsyncTestHelpers.WaitUntilAsync(() => isControlRequested, TimeSpan.FromMilliseconds(Timeout),
            message: "The joined url( of the diagram was not loaded");

        // Assert
        var svg = await diagram.InnerHTMLAsync();
        Assert.Contains("mermaid-exfiltration.test/space", svg);
        Assert.Contains("mermaid-exfiltration.test/comment", svg);
        Assert.Contains("mermaid-exfiltration.test/set", svg);
        Assert.False(isRequested);
    }
}
