using System.Text.RegularExpressions;
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
    // The page that tests overwrite. Each of them writes its own content before it opens the page.
    private const string ScratchPage = "Scratch.md";

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

    [Fact]
    public async Task WhenBlockIsSavedInItsDialogAndThenThePageIsSaved_ThenTheSaveSucceedsAndEachRegionHasOneDecoration()
    {
        // Arrange
        const string source =
            "---\ntitle: Scratch\n---\n\n# Scratch\n\n" +
            ":: HomeBlaze.Samples.Motor#scratchmotor Name=ScratchMotor TargetSpeed=1200 SimulationInterval=00:00:01\n" +
            "::\n\nSpeed {{ scratchmotor/TargetSpeed }}\n";
        await _fixture.WriteDemoPageAsync(ScratchPage, source);

        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await Assertions.Expect(page.GetByTitle(new Regex(@"^Automatic \("))).ToBeVisibleAsync(new() { Timeout = 30000 });
        await page.GetByTestId("edit-mode-menu").ClickAsync();
        await page.Locator(".mud-popover-open").GetByText("Split", new() { Exact = true }).ClickAsync();

        // The editor only knows its block regions once the decorations are applied.
        await Assertions.Expect(page.Locator(".subject-block-decoration").First).ToBeVisibleAsync(new() { Timeout = 30000 });
        var headLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "HomeBlaze.Samples.Motor#scratchmotor" });
        await Assertions.Expect(headLine).ToBeVisibleAsync(new() { Timeout = 30000 });
        await headLine.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit scratchmotor" }).ClickAsync();

        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.GetByLabel("Target Speed (RPM)").FillAsync("1500");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        var writtenHeadLine = page.Locator(".monaco-editor .view-line", new() { HasTextString = "TargetSpeed=1500" });
        await Assertions.Expect(writtenHeadLine).ToBeVisibleAsync(new() { Timeout = 30000 });

        // The notification of the dialog is closed, because a notification with the same text is not shown twice.
        var savedNotification = page.Locator(".mud-snackbar").Filter(new() { HasText = "Configuration saved." });
        await Assertions.Expect(savedNotification).ToBeVisibleAsync(new() { Timeout = 30000 });
        await savedNotification.GetByRole(AriaRole.Button).ClickAsync();
        await Assertions.Expect(savedNotification).ToHaveCountAsync(0, new() { Timeout = 30000 });

        // Act
        await page.Locator("[data-testid='save-button'] button").ClickAsync();

        // Assert: the notification comes after the last refresh of the decorations. Refreshes that overlapped
        // would each have added their decorations, and one of them would have stayed besides the later ones.
        await Assertions.Expect(savedNotification).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Assertions.Expect(page.GetByText("Error saving configuration")).ToHaveCountAsync(0);

        var decorationCounts = await page.EvaluateAsync<int[]>(
            """
            () => {
                const decorations = blazorMonaco.editor.getEditor('markdown-editor').getModel().getAllDecorations();
                return ['subject-block-decoration', 'expression-decoration']
                    .map(className => decorations.filter(decoration => decoration.options.className === className).length);
            }
            """);
        Assert.Equal([1, 1], decorationCounts);

        var written = (await _fixture.ReadFileAsync(GetDemoPagePath(ScratchPage))).Replace("\r\n", "\n");
        Assert.Equal(source.Replace("TargetSpeed=1200", "TargetSpeed=1500"), written);
    }

    private static string GetDemoPagePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "Demo", name);
    }
}
