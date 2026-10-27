using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

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

    private readonly PlaywrightFixture _fixture;

    public MermaidDiagramTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IPage> OpenPageAsync(List<string>? requestUrls = null)
    {
        var page = await _fixture.CreatePageAsync();
        if (requestUrls is not null)
        {
            page.Request += (_, request) => requestUrls.Add(request.Url);
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
        await Assertions.Expect(diagram.Locator("svg")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenDiagramContainsActiveContent_ThenNothingActiveIsRenderedOrLoaded()
    {
        // Arrange
        var requestUrls = new List<string>();

        // Act
        var page = await OpenPageAsync(requestUrls);
        var diagram = page.Locator("pre.mermaid").Nth(ActiveContentDiagram);
        await Assertions.Expect(diagram).ToHaveAttributeAsync("data-mermaid-state", "done", new() { Timeout = Timeout });

        // Assert
        await Assertions.Expect(diagram.Locator("img")).ToHaveCountAsync(0);
        await Assertions.Expect(diagram.Locator("a")).ToHaveCountAsync(0);
        await Assertions.Expect(diagram.Locator("foreignObject")).ToHaveCountAsync(0);
        var hasEventHandler = await diagram.EvaluateAsync<bool>(
            "element => [...element.querySelectorAll('*')].some(child => [...child.attributes].some(attribute => attribute.name.startsWith('on')))");
        Assert.False(hasEventHandler);
        Assert.DoesNotContain(requestUrls, url => url.Contains("127.0.0.1:9", StringComparison.Ordinal));
    }
}
