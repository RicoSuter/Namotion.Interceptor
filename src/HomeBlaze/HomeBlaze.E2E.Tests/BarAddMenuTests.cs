using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the "+" in the bar of a selected block of a page in inline edit mode, which adds a widget or text to each
/// slot of the block, or an item to a list slot of configured items.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class BarAddMenuTests
{
    private const int Timeout = 30000;
    private const string TextEditorId = "markdown-text-editor";
    private const string ScratchPageName = "Scratch.md";
    private const string EmptyContentPageName = "EmptyContent.md";

    // A card with a body only and a tab layout, whose tabs are configured items.
    private const string CardAndTabsContent =
        "---\n"
        + "title: Bar Add Demo\n"
        + "---\n"
        + "\n"
        + "# Bar Add Demo\n"
        + "\n"
        + ":: HomeBlaze.Components.Card Title=\"Add Card\"\n"
        + "Card body.\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.TabLayout\n"
        + "::: Tab Title=\"First Tab\"\n"
        + "First tab text.\n"
        + ":::\n"
        + "::\n";

    // An empty Alert, whose body has no content of its own, and a flow with one item.
    private const string EmptyContent =
        "---\n"
        + "title: Empty Content Demo\n"
        + "---\n"
        + "\n"
        + "# Empty Content Demo\n"
        + "\n"
        + ":: HomeBlaze.Components.Alert\n"
        + "::\n"
        + "\n"
        + ":: HomeBlaze.Components.FlowLayout MinWidth=200\n"
        + "::: Item\n"
        + "First item.\n"
        + ":::\n"
        + "::: Item\n"
        + ":::\n"
        + "::\n";

    private readonly PlaywrightFixture _fixture;

    public BarAddMenuTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenACardIsSelected_ThenItsPlusOffersAWidgetAndTextForEachSlotInOrder()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(ScratchPageName, CardAndTabsContent);
        var actionBar = await page.GetCard("Add Card").Locator(".mud-card-header").SelectWidgetAsync();

        // Act
        await actionBar.GetByRole(AriaRole.Button, new() { Name = "Add to Add Card", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(GetOpenMenuItems(page)).ToHaveTextAsync(
            [
                "Add widget...",
                "Add text...",
                "Add widget to Header right...",
                "Add text to Header right...",
                "Add widget to Footer...",
                "Add text to Footer..."
            ],
            new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTextIsAddedToTheFooterOfACard_ThenAFooterBlockHoldsItAndTheTextIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(ScratchPageName, CardAndTabsContent);
        var actionBar = await page.GetCard("Add Card").Locator(".mud-card-header").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("add-menu").GetByRole(AriaRole.Button).ClickAsync();
        await GetOpenMenuItem(page, "Add text to Footer...").ClickAsync();
        await SetTextEditorValueAsync(page, "Footer text");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            ScratchPageName,
            text => text.Contains(":: HomeBlaze.Components.Card Title=\"Add Card\"\nCard body.\n::: Footer\n\nFooter text\n\n:::\n::\n"),
            "The text was not written into a footer block.");
        var footer = page.GetCard("Add Card").GetByTestId("card-footer");
        await Assertions.Expect(footer.GetByText("Footer text", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetTextWrapper(page, "Footer text")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAWidgetIsAddedToTheBodyOfACard_ThenItFollowsTheBodyAndIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(ScratchPageName, CardAndTabsContent);
        var actionBar = await page.GetCard("Add Card").Locator(".mud-card-header").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("add-menu").GetByRole(AriaRole.Button).ClickAsync();
        await GetOpenMenuItem(page, "Add widget...").ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByTestId("type-search-input").FillAsync("Alert");
        await dialog.GetByTestId("type-row-alert").ClickAsync();
        await dialog.GetByTestId("create-name-input").FillAsync("bodyalert");
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            ScratchPageName,
            text => text.Contains("Card body.\n\n::: HomeBlaze.Components.Alert#bodyalert"),
            "The widget was not added to the body of the card.");
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected [data-testid='alert']")).ToHaveCountAsync(1, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATabLayoutIsSelected_ThenItsPlusOpensTheSetupDialogOfATabWithoutAMenuAndTheLayoutStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(ScratchPageName, CardAndTabsContent);
        var actionBar = await page.GetByTestId("tab-panel").GetByText("First tab text.", new() { Exact = true }).SelectWidgetAsync();
        await Assertions.Expect(actionBar.GetByTestId("add-menu")).ToHaveCountAsync(0);

        // Act
        await actionBar.GetByRole(AriaRole.Button, new() { Name = "Add Tab", Exact = true }).ClickAsync();
        var dialog = page.GetByTestId("create-subject-dialog");
        await dialog.GetByLabel("Title", new() { Exact = true }).FillAsync("Second Tab");
        await page.GetByTestId("create-button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            ScratchPageName,
            text => text.Contains("First tab text.\n:::\n::: Tab Title=\"Second Tab\""),
            "The tab was not added.");
        await Assertions.Expect(page.GetByTestId("tab-layout").GetByRole(AriaRole.Tab, new() { Name = "Second Tab" })).ToBeVisibleAsync(new() { Timeout = Timeout });

        // A tab has no widget of its own to select, so the layout keeps the selection.
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected:has(> [data-testid='tab-layout'])")).ToHaveCountAsync(1, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenAnItemWithTextIsAddedToAFlow_ThenTheFlowHasANewItemWithTheTextAndItIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(EmptyContentPageName, EmptyContent);
        var actionBar = await page.GetByText("First item.", new() { Exact = true }).SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("add-menu").GetByRole(AriaRole.Button).ClickAsync();
        await GetOpenMenuItem(page, "Add Item with text...").ClickAsync();
        await SetTextEditorValueAsync(page, "Added item.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            EmptyContentPageName,
            text => text.Contains("::: Item\n:::\n::: Item\n\nAdded item.\n\n:::\n::\n"),
            "The item with the text was not added.");
        await Assertions.Expect(page.GetByTestId("flow-item")).ToHaveCountAsync(3, new() { Timeout = Timeout });
        await Assertions.Expect(GetTextWrapper(page, "Added item.")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenTheTextDialogOfANewItemIsCanceled_ThenThePageIsUnchanged()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(EmptyContentPageName, EmptyContent);
        var actionBar = await page.GetByText("First item.", new() { Exact = true }).SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("add-menu").GetByRole(AriaRole.Button).ClickAsync();
        await GetOpenMenuItem(page, "Add Item with text...").ClickAsync();
        await SetTextEditorValueAsync(page, "Never saved.");
        await page.GetByTestId("markdown-text-cancel").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();

        // Assert
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.GetByTestId("flow-item")).ToHaveCountAsync(2);
        Assert.Equal(EmptyContent, (await _fixture.ReadFileAsync(_fixture.GetDemoPagePath(EmptyContentPageName))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WhenTextIsAddedToAnEmptyAlert_ThenItIsTheBodyOfTheAlertAndIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(EmptyContentPageName, EmptyContent);
        var actionBar = await page.GetByTestId("alert").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("add-menu").GetByRole(AriaRole.Button).ClickAsync();
        await GetOpenMenuItem(page, "Add text...").ClickAsync();
        await SetTextEditorValueAsync(page, "Alert text.");
        await page.GetByTestId("markdown-text-save").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            EmptyContentPageName,
            text => text.Contains(":: HomeBlaze.Components.Alert\n\nAlert text.\n\n::\n"),
            "The text was not written into the alert.");
        await Assertions.Expect(page.GetByTestId("alert").GetByText("Alert text.", new() { Exact = true })).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(GetTextWrapper(page, "Alert text.")).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenThePlusOfACardIsDoubleClicked_ThenNoEditDialogOpens()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync(ScratchPageName, CardAndTabsContent);
        var actionBar = await page.GetCard("Add Card").Locator(".mud-card-header").SelectWidgetAsync();
        var plus = actionBar.GetByTestId("add-menu").GetByRole(AriaRole.Button);

        // Act: only the double click event, because the clicks of a real double click open and close the menu in renders
        // that a further click could overtake.
        await plus.DispatchEventAsync("dblclick");

        // Assert: the dialog of new text, which the menu then opens, is the only one, and a dialog that the double click
        // opened would have come first.
        await plus.ClickAsync();
        await GetOpenMenuItem(page, "Add text...").ClickAsync();
        await ExpectTextEditorValueAsync(page, string.Empty);
        await Assertions.Expect(page.GetByTestId("subject-edit-dialog-type")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByRole(AriaRole.Dialog)).ToHaveCountAsync(1);
    }

    private async Task<IPage> OpenPageInInlineModeAsync(string pageName, string content)
    {
        await _fixture.WriteDemoPageAsync(pageName, content);
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{pageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        await page.SwitchToInlineModeAsync();
        return page;
    }

    private static ILocator GetOpenMenuItems(IPage page) => page.Locator(".mud-popover-open").GetByRole(AriaRole.Menuitem);

    private static ILocator GetOpenMenuItem(IPage page, string name) =>
        page.Locator(".mud-popover-open").GetByRole(AriaRole.Menuitem, new() { Name = name, Exact = true });

    private static ILocator GetTextWrapper(IPage page, string text) =>
        page.Locator(".subject-component-wrapper.selectable:has(> .markdown-text)").Filter(new() { HasText = text });

    // The dialog edits its text in Monaco, which has no input value, so the text is read and written through the editor.
    private static Task ExpectTextEditorValueAsync(IPage page, string expected) =>
        page.WaitForFunctionAsync(
            $"expected => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true)?.getValue() === expected",
            expected,
            new() { Timeout = Timeout });

    private static async Task SetTextEditorValueAsync(IPage page, string text)
    {
        await page.WaitForFunctionAsync(
            $"() => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true) != null", null, new() { Timeout = Timeout });
        await page.EvaluateAsync($"text => blazorMonaco.editor.getEditor('{TextEditorId}').setValue(text)", text);
    }
}
