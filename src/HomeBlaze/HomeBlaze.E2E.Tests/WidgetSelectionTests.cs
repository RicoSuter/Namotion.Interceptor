using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Samples;
using HomeBlaze.Storage.Files;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for the selection of a widget in inline edit mode and the bar with the actions of the selected widget.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class WidgetSelectionTests
{
    private const int Timeout = 30000;
    private const string PageName = "Selection.md";

    private readonly PlaywrightFixture _fixture;

    public WidgetSelectionTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenSelectParentIsClickedOnANestedWidget_ThenItsParentIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "indicator").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("select-parent-button").ClickAsync();

        // Assert: the card around the indicator is selected, and it has no parent to select.
        var selected = page.Locator(".subject-component-wrapper.selected");
        await Assertions.Expect(selected).ToContainTextAsync("Selection Card", new() { Timeout = Timeout });
        await Assertions.Expect(selected.GetByTestId("card-header-right")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("select-parent-button")).ToHaveCountAsync(0, new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenEscapeIsPressed_ThenTheSelectionIsCleared()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await page.Keyboard.PressAsync("Escape");

        // Assert
        await Assertions.Expect(page.GetWidgetActionBar()).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selected")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenEditIsClickedInTheActionBar_ThenTheEditDialogOfTheSelectedWidgetOpens()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();

        // Assert: the switch has no title or label, so the dialog is named "Edit" and its type tells which widget it edits.
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Edit", Exact = true });
        await Assertions.Expect(dialog.GetByLabel("Path", new() { Exact = true }))
            .ToHaveValueAsync("selectionmotor/IsPowered", new() { Timeout = Timeout });
        await Assertions.Expect(dialog.GetByTestId("subject-edit-dialog-type")).ToHaveTextAsync("HomeBlaze.Components.Switch");
    }

    [Fact]
    public async Task WhenAWidgetIsSelectedInDarkMode_ThenItsOutlineAndEditActionHaveTheLightenedPrimaryColor()
    {
        // Arrange: the app starts in the dark theme.
        var page = await OpenPageInInlineModeAsync();
        var wrapper = GetWrapperOf(page, "switch");

        // Act
        var actionBar = await wrapper.SelectWidgetAsync();

        // Assert
        var expected = await page.ResolveColorAsync("var(--mud-palette-primary-lighten)");
        Assert.NotEqual(await page.ResolveColorAsync("var(--mud-palette-primary)"), expected);
        await Assertions.Expect(wrapper).ToHaveCSSAsync("outline-color", expected, new() { Timeout = Timeout });
        await Assertions.Expect(actionBar.GetByTestId("edit-subject-button")).ToHaveCSSAsync("color", expected);
    }

    [Fact]
    public async Task WhenEditIsClickedOnACardWithATitle_ThenTheEditDialogIsTitledWithItAndShowsTheType()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await page.GetByText("Body of the selection card.").SelectWidgetAsync();

        // Act
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Edit Selection Card", Exact = true });
        await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(dialog.GetByTestId("subject-edit-dialog-type")).ToHaveTextAsync("HomeBlaze.Components.Card");
    }

    [Fact]
    public async Task WhenEscapeIsPressedInTheEditDialog_ThenTheWidgetStaysSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var actionBar = await GetWrapperOf(page, "switch").SelectWidgetAsync();
        await actionBar.GetByTestId("edit-subject-button").ClickAsync();
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByLabel("Path", new() { Exact = true }))
            .ToHaveValueAsync("selectionmotor/IsPowered", new() { Timeout = Timeout });

        // A text field ignores the shortcuts of the page anyway, so the focus is on a button of the dialog.
        var closeButton = dialog.Locator(".mud-button-close");
        await closeButton.FocusAsync();

        // Act: the close comes after the key on the same connection, so the key has been handled once the dialog is gone.
        await page.Keyboard.PressAsync("Escape");
        await closeButton.ClickAsync();

        // Assert
        await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(GetWrapperOf(page, "switch")).ToHaveClassAsync(new Regex(@"\bselected\b"));
        await Assertions.Expect(page.GetWidgetActionBar()).ToBeVisibleAsync();
    }

    [Fact]
    public async Task WhenANestedWidgetWithoutConfigurationIsSelected_ThenItsBarOffersOnlyItsParent()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var wrapper = GetValueListWrapper(page, "Nested Row");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var actionBar = await wrapper.SelectWidgetAsync();

        // Assert
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\bselected\b"));
        await Assertions.Expect(actionBar.GetByTestId("select-parent-button")).ToBeVisibleAsync();
        await Assertions.Expect(actionBar.GetByTestId("edit-subject-button")).ToHaveCountAsync(0);

        // Act
        await actionBar.GetByTestId("select-parent-button").ClickAsync();

        // Assert
        var selected = page.Locator(".subject-component-wrapper.selected");
        await Assertions.Expect(selected).ToContainTextAsync("List Card", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar().GetByTestId("edit-subject-button")).ToBeVisibleAsync(new() { Timeout = Timeout });
    }

    [Fact]
    public async Task WhenATopLevelWidgetWithoutConfigurationIsSelected_ThenItHasNoBar()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var wrapper = GetValueListWrapper(page, "Top Level Row");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await wrapper.ClickAsync(new() { Force = true });

        // Assert
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\bselected\b"), new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar()).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task WhenAWidgetShowsNothing_ThenItIsHiddenInEditMode()
    {
        // Arrange
        var page = await OpenPageAsync();
        var wrapper = page.Locator(".subject-component-wrapper.selectable[aria-label='Value map']");

        // Act
        await page.SwitchToInlineModeAsync();

        // Assert: a hidden element is no tab stop.
        await Assertions.Expect(wrapper).ToHaveCountAsync(1, new() { Timeout = Timeout });
        await Assertions.Expect(wrapper).ToHaveCSSAsync("display", "none");
    }

    [Fact]
    public async Task WhenTheSubjectOfAWidgetBlockIsClicked_ThenItIsSelectedAndItsParentIsTheWidgetBlock()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var widgetBlock = page.GetByRole(AriaRole.Group, new() { Name = "selectionmotor", Exact = true });
        var innerSubject = widgetBlock.GetByRole(AriaRole.Group, new() { Name = "SelectionMotor", Exact = true });

        // Act
        var actionBar = await innerSubject.GetByText("SelectionMotor", new() { Exact = true }).SelectWidgetAsync();

        // Assert
        await Assertions.Expect(innerSubject).ToHaveAttributeAsync("aria-current", "true", new() { Timeout = Timeout });
        await Assertions.Expect(widgetBlock).Not.ToHaveAttributeAsync("aria-current", "true");

        // Act
        await actionBar.GetByTestId("select-parent-button").ClickAsync();

        // Assert
        await Assertions.Expect(widgetBlock).ToHaveAttributeAsync("aria-current", "true", new() { Timeout = Timeout });
        await Assertions.Expect(innerSubject).Not.ToHaveAttributeAsync("aria-current", "true");
    }

    [Fact]
    public async Task WhenSpaceIsPressedOnAFocusedWidget_ThenItIsSelected()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var wrapper = GetWrapperOf(page, "switch");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });
        await wrapper.FocusAsync();

        // Act
        await page.Keyboard.PressAsync("Space");

        // Assert
        await Assertions.Expect(wrapper).ToHaveAttributeAsync("aria-current", "true", new() { Timeout = Timeout });
        await Assertions.Expect(page.GetWidgetActionBar()).ToBeVisibleAsync();
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("slider")]
    [InlineData("operation-button")]
    public async Task WhenAControlIsClickedInInlineMode_ThenItsWidgetIsSelectedAndTheMotorIsUnchanged(string testId)
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var motor = GetMotor();
        Assert.True(motor.IsPowered);
        Assert.Equal(1000, motor.TargetSpeed);

        var control = page.GetByTestId(testId);
        await Assertions.Expect(control).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selectable").First).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act: a click as a user makes it, at the middle of the control, which would toggle, set or invoke on a viewed page.
        var box = (await control.BoundingBoxAsync())!;
        await page.Mouse.ClickAsync(box.X + box.Width / 2, box.Y + box.Height / 2);

        // Assert: the bar comes with a render after the click, so whatever the click caused has arrived on the server.
        await Assertions.Expect(page.GetWidgetActionBar()).ToBeVisibleAsync(new() { Timeout = Timeout });
        var wrapper = GetWrapperOf(page, testId);
        await Assertions.Expect(wrapper).ToHaveClassAsync(new Regex(@"\bselected\b"));
        await Assertions.Expect(wrapper).ToHaveAttributeAsync("role", "group");
        await Assertions.Expect(wrapper).ToHaveAttributeAsync("aria-roledescription", "widget");
        await Assertions.Expect(wrapper).ToHaveAttributeAsync("aria-current", "true");
        await Assertions.Expect(wrapper).ToHaveAttributeAsync("aria-label", new Regex(@"\S"));
        await Assertions.Expect(page.GetByRole(AriaRole.Toolbar, new() { Name = "Widget actions" })
            .GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true })).ToBeVisibleAsync();
        Assert.True(motor.IsPowered);
        Assert.Equal(1000, motor.TargetSpeed);
    }

    [Fact]
    public async Task WhenTabIsPressedOnTheSelectedWidget_ThenTheBarIsFocusedAndEscapeReturnsTheFocusToTheWidget()
    {
        // Arrange
        var page = await OpenPageInInlineModeAsync();
        var wrapper = GetWrapperOf(page, "switch");
        await Assertions.Expect(wrapper).ToBeVisibleAsync(new() { Timeout = Timeout });
        await wrapper.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var editButton = page.GetWidgetActionBar().GetByTestId("edit-subject-button");
        await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("Tab");

        // Assert
        await Assertions.Expect(editButton).ToBeFocusedAsync(new() { Timeout = Timeout });

        // Act
        await page.Keyboard.PressAsync("Escape");

        // Assert
        await Assertions.Expect(page.GetWidgetActionBar()).ToHaveCountAsync(0, new() { Timeout = Timeout });
        await Assertions.Expect(wrapper).ToBeFocusedAsync(new() { Timeout = Timeout });
    }

    private async Task<IPage> OpenPageInInlineModeAsync()
    {
        var page = await OpenPageAsync();
        await page.SwitchToInlineModeAsync();
        return page;
    }

    private async Task<IPage> OpenPageAsync()
    {
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync(Timeout);
        return page;
    }

    // The innermost widget around the element with the test id.
    private static ILocator GetWrapperOf(IPage page, string testId) =>
        page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])")
            .Filter(new() { HasNot = page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])") })
            .First;

    private static ILocator GetValueListWrapper(IPage page, string rowLabel) =>
        page.Locator(".subject-component-wrapper.selectable:has(> [data-testid='value-list'])")
            .Filter(new() { HasText = rowLabel });

    private Motor GetMotor() =>
        (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["selectionmotor"];
}
