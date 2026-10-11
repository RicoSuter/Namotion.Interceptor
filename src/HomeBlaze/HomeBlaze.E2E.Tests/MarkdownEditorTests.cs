using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for advanced markdown editing with WYSIWYG features.
/// Tests Monaco decorations, block editing, and expression editing.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class MarkdownEditorTests
{
    private readonly PlaywrightFixture _fixture;

    public MarkdownEditorTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenCursorIsOnBlockHead_ThenEditButtonNamesTheBlock()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Navigate to Inline.md (has the :: HomeBlaze.Samples.Motor#mymotor block)
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Inline.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Open Split mode via dropdown menu
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Split").ClickAsync();

        // Wait for Monaco editor to appear
        var monacoEditor = page.Locator(".monaco-editor");
        await Assertions.Expect(monacoEditor).ToBeVisibleAsync(new() { Timeout = 30000 });

        // The editor only knows its block regions once the decorations are applied, a round trip
        // after .monaco-editor appears. A cursor that is placed earlier is not matched against them.
        await Assertions.Expect(page.Locator(".subject-block-decoration").First).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act
        // Click on the head line of the motor block to put the cursor inside it
        var headLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "HomeBlaze.Samples.Motor" });
        await Assertions.Expect(headLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await headLine.ClickAsync();

        // Assert: the edit button names the block
        var editButton = page.GetByRole(AriaRole.Button, new() { Name = "Edit mymotor" });
        await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Fact]
    public async Task WhenCursorIsInsideExpression_ThenEditExpressionButtonIsVisible()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Navigate to Inline.md (has {{ mymotor/Temperature }})
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Inline.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Open Split mode via dropdown menu
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Split").ClickAsync();

        // Wait for Monaco editor
        var monacoEditor = page.Locator(".monaco-editor");
        await Assertions.Expect(monacoEditor).ToBeVisibleAsync(new() { Timeout = 30000 });

        // The editor only knows its expression regions once the decorations are applied, a round trip
        // after .monaco-editor appears. A cursor that is placed earlier is not matched against them.
        await Assertions.Expect(page.Locator(".expression-decoration").First).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act
        // The line ends with the expression, so one step left from the end of the line is inside it
        var expressionLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "mymotor/Temperature" });
        await Assertions.Expect(expressionLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await expressionLine.ClickAsync();
        await page.Keyboard.PressAsync("End");
        await page.Keyboard.PressAsync("ArrowLeft");

        // Assert
        var editButton = page.Locator("[data-testid='edit-expression-button']");
        await Assertions.Expect(editButton).ToBeVisibleAsync(new() { Timeout = 10000 });
        await Assertions.Expect(editButton).ToHaveTextAsync("Edit Expression");
    }

    [Fact]
    public async Task WhenInlineModeIsEnabled_ThenWidgetEditButtonsAreVisible()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Navigate to Inline.md (has a motor widget)
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Inline.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act
        // Open Inline mode via dropdown menu (enables IsEditing on widgets)
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Inline").ClickAsync();

        // Assert
        // After enabling edit mode, edit buttons on widgets should be visible
        var editButtonAfter = page.Locator("[data-testid='edit-subject-button']").First;
        await Assertions.Expect(editButtonAfter).ToBeVisibleAsync(new() { Timeout = 10000 });
    }

    [Fact]
    public async Task WhenSplitModeIsOpened_ThenBlocksAreHighlighted()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Navigate to Inline.md
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Inline.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act
        // Open Split mode via dropdown menu
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
        await editMenu.ClickAsync();
        await page.GetByText("Split").ClickAsync();

        // Wait for Monaco editor
        var monacoEditor = page.Locator(".monaco-editor");
        await Assertions.Expect(monacoEditor).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Assert: the decorations are applied from the editor's OnDidInit callback, a round trip
        // after .monaco-editor appears, so assert on the locator, which retries, instead of
        // counting once.
        var decorations = page.Locator(".subject-block-decoration");
        await Assertions.Expect(decorations.First).ToBeVisibleAsync(new() { Timeout = 30000 });
    }
}
