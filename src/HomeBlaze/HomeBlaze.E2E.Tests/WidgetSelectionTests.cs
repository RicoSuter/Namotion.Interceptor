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

        // Assert
        var dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByLabel("Path", new() { Exact = true }))
            .ToHaveValueAsync("selectionmotor/IsPowered", new() { Timeout = Timeout });
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
        await Assertions.Expect(GetWrapperOf(page, testId)).ToHaveClassAsync(new Regex(@"\bselected\b"));
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
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \(")))
            .ToBeVisibleAsync(new() { Timeout = Timeout });

        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = Timeout });
        await editMenu.ClickAsync();
        await page.GetByText("Inline").ClickAsync();
        await Assertions.Expect(page.Locator(".subject-component-wrapper.selectable").First).ToBeVisibleAsync(new() { Timeout = Timeout });
        return page;
    }

    // The innermost widget around the element with the test id.
    private static ILocator GetWrapperOf(IPage page, string testId) =>
        page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])")
            .Filter(new() { HasNot = page.Locator($".subject-component-wrapper.selectable:has([data-testid='{testId}'])") })
            .First;

    private Motor GetMotor() =>
        (Motor)((MarkdownFile)_fixture.GetDemoFolder().Children[PageName]).Children["selectionmotor"];
}
