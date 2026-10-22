using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the structure editing of a markdown page in inline edit mode: delete, move and insert blocks, the items
/// of list slots, the widgets that render nothing, the content of a Responsive block and the preview of the Split mode.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class InlineStructureEditingTests
{
    private const int Timeout = 30000;
    private const string PageName = "Structure.md";

    // The server writes the page in its output folder, so a run that ends with a change of the page would change the
    // baseline of the next run if it were read from there.
    private const string Content =
        "---\n"
        + "title: Structure Demo\n"
        + "---\n"
        + "\n"
        + "# Structure Demo\n"
        + "\n"
        + ":: HomeBlaze.Samples.Motor#structuremotor Name=StructureMotor TargetSpeed=1000 SimulationInterval=00:00:01\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.Switch Path=structuremotor/IsPowered\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.ValueList\n"
        + "::: Value Path=structuremotor/TargetSpeed Label=\"Row A\"\n"
        + ":::\n"
        + "::: Value Path=structuremotor/CurrentSpeed Label=\"Row B\"\n"
        + ":::\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.ValueMap#structuremap\n"
        + "::: Case Value=Stopped Text=\"Off\"\n"
        + ":::\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.Responsive To=Xs\n"
        + "::: HomeBlaze.Components.Card Title=\"Phone Card\"\n"
        + "Only on phones.\n"
        + ":::\n"
        + "::\n";

    private readonly PlaywrightFixture _fixture;

    public InlineStructureEditingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenDeleteIsConfirmed_ThenTheBlockIsRemovedFromThePage()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act: the bar has a button named Delete as well, so the one of the dialog is looked up in the dialog.
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => !text.Contains("HomeBlaze.Components.Switch"), "The switch block was not deleted.");
        await Assertions.Expect(page.GetByTestId("switch")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenDeleteIsCanceled_ThenThePageIsUnchanged()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        Assert.Equal(Content, (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(PageName))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WhenABlockIsMovedDown_ThenItSwapsWithTheNextBlockInTheFile()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("move-down-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.IndexOf("HomeBlaze.Components.ValueList", StringComparison.Ordinal) <
                    text.IndexOf("HomeBlaze.Components.Switch", StringComparison.Ordinal),
            "The switch was not moved below the value list.");
    }

    [Fact]
    public async Task WhenTheInlineModeIsLeft_ThenTheStructureActionsAreGone()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.GetByTestId("delete-block-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenThePageIsEditedInline_ThenAValueMapIsAChipWithItsName()
    {
        // Arrange & Act
        var page = await OpenPageInInlineModeAsync();

        // Assert
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAValueMapChipIsSelected_ThenItsCasesAreListedAndACaseCanBeAdded()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await chip.SelectWidgetAsync();
        var cases = page.GetByTestId("value-map-cases");
        await Assertions.Expect(cases.GetByTestId("value-map-case")).ToHaveCountAsync(1, new() { Timeout = Timeout });

        // Act
        await cases.GetByTestId("add-case-button").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByLabel("Value", new() { Exact = true }).FillAsync("Running");

        // The actions of a dialog stand outside the element with its test id.
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName, text => text.Contains("::: Case Value=Stopped Text=\"Off\"\n:::\n::: Case Value=Running"), "The case was not added.");
    }

    [Fact]
    public async Task WhenThePageIsEditedInline_ThenTheChipCoversNoContent()
    {
        // Arrange & Act
        var page = await OpenPageInInlineModeAsync();
        var chip = page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" });
        await Assertions.Expect(chip).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Assert: neither a line of text nor a block of the content around the chips reaches into the chip.
        var coveredContent = await chip.EvaluateAsync<string[]>(
            """
            chip => {
                const box = chip.getBoundingClientRect();
                const stack = chip.closest('.invisible-widget-stack');
                const content = stack.parentElement;
                const overlaps = rectangle => rectangle.width > 0 && rectangle.height > 0 &&
                    rectangle.left < box.right && rectangle.right > box.left && rectangle.top < box.bottom && rectangle.bottom > box.top;
                const covered = [];
                for (const block of content.querySelectorAll(':scope > .subject-component-wrapper')) {
                    if (overlaps(block.getBoundingClientRect())) {
                        covered.push(block.getAttribute('aria-label') ?? 'block');
                    }
                }

                const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
                for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                    if (stack.contains(node) || !node.textContent.trim()) {
                        continue;
                    }

                    const range = document.createRange();
                    range.selectNodeContents(node);
                    for (const line of range.getClientRects()) {
                        if (overlaps(line)) {
                            covered.push(node.textContent.trim());
                        }
                    }
                }

                return covered;
            }
            """);
        Assert.Empty(coveredContent);
    }

    [Fact]
    public async Task WhenACaseInTheListOfAValueMapIsDeleted_ThenItIsRemovedFromThePage()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await page.GetByTestId("invisible-widget-chip").Filter(new() { HasText = "structuremap" }).SelectWidgetAsync();
        var actionBar = await page.GetByTestId("value-map-cases").GetByTestId("value-map-case").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("delete-block-button").ClickAsync();
        await page.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            PageName,
            text => text.Contains("HomeBlaze.Components.ValueMap#structuremap") && !text.Contains("Case Value=Stopped"),
            "The case was not deleted.");
    }

    [Fact]
    public async Task WhenThePageIsViewed_ThenNoChipIsShown()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();

        // Act
        await page.SelectEditModeAsync("View", Timeout);

        // Assert
        await Assertions.Expect(page.GetByTestId("invisible-widget-chip")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    private async Task<IPage> OpenPageInInlineModeAsync()
    {
        await _fixture.WriteDemoPageAsync(PageName, Content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        return page;
    }

    // The innermost widget around the element with the test id.
    private static ILocator GetWrapperOf(IPage page, string testId) =>
        page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])")
            .Filter(new() { HasNot = page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])") })
            .First;
}
