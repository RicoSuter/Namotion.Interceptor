using System.Text.Json;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for page editing functionality (split-view editor).
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PageEditTests
{
    // The page that tests overwrite. Each of them writes its own content before it opens the page.
    private const string ScratchPage = "Scratch.md";

    private readonly PlaywrightFixture _fixture;

    public PageEditTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task EditModeMenu_ShouldBeVisibleOnMarkdownPage()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - Navigate to Dashboard (a markdown page)
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - Edit mode menu should be visible
        var editMenu = page.Locator("[data-testid='edit-mode-menu']");
        await Assertions.Expect(editMenu).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task SplitMode_ShouldOpenSplitView()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();

        // Act - Open Split mode via dropdown menu
        await page.SelectEditModeAsync("Split");

        // Assert - Split view should be visible
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task SplitView_ShouldHaveSaveButton()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();

        // Act - Open Split mode via dropdown menu
        await page.SelectEditModeAsync("Split");

        // Wait for split view to appear
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Assert - Save button should be visible
        var saveButton = page.Locator("[data-testid='save-button']");
        await Assertions.Expect(saveButton).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task ViewMode_ShouldCloseEditMode()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();

        // Act - Open Split mode
        await page.SelectEditModeAsync("Split");

        // Verify split view is open
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Switch to View mode
        await page.SelectEditModeAsync("View");

        // Assert - Split view should no longer be visible
        await Assertions.Expect(splitter).Not.ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task SourceMode_ShouldShowEditorOnly()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Dashboard.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();

        // Act - Open Source mode via dropdown menu
        await page.SelectEditModeAsync("Source");

        // Assert - Monaco editor should be visible (not in split view)
        var monacoEditor = page.Locator(".monaco-editor");
        await Assertions.Expect(monacoEditor).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Split view should NOT be visible
        var splitter = page.Locator("[data-testid='edit-splitter']");
        await Assertions.Expect(splitter).Not.ToBeVisibleAsync(new() { Timeout = 5000 });
    }

    [Fact]
    public async Task WhenThePageIsEditedInTheSourceModeOnAPhone_ThenATextAreaShowsTheTextAndSavesTheChange()
    {
        // Arrange: Monaco does not support the browsers of phones, so a device whose pointer is coarse gets a text area.
        const string source = "# Scratch\n\nText before the edit\n";
        await _fixture.WriteDemoPageAsync(ScratchPage, source);
        var page = await _fixture.CreatePageAsync(
            new BrowserNewContextOptions { HasTouch = true, IsMobile = true, ViewportSize = new() { Width = 375, Height = 667 } });
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitUntilInteractiveAsync();
        await page.SelectEditModeAsync("Source");

        var textArea = page.GetByTestId("markdown-source-text");
        await Assertions.Expect(textArea).ToHaveValueAsync(source, new() { Timeout = 30000 });
        await Assertions.Expect(page.Locator(".monaco-editor")).ToHaveCountAsync(0);

        // Act
        await textArea.FillAsync(source.Replace("before", "after"));
        await page.Locator("[data-testid='save-button'] button").ClickAsync();

        // Assert
        await _fixture.WaitForDemoPageTextAsync(
            ScratchPage, text => text == source.Replace("before", "after"), "The text of the text area was not saved.");
    }

    [Fact]
    public async Task WhenTheSourceModeIsLeft_ThenItsEditorAndTheModelOfTheEditorAreDisposed()
    {
        // Arrange
        var page = await OpenWidgetsDemoPageAsync();
        await page.SelectEditModeAsync("Source");
        await Assertions.Expect(page.Locator(".monaco-editor")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await page.WaitForFunctionAsync("() => monaco.editor.getEditors().length === 1", null, new() { Timeout = 30000 });

        // Act
        await page.SelectEditModeAsync("View");

        // Assert
        await page.WaitForFunctionAsync(
            "() => monaco.editor.getEditors().length === 0 && monaco.editor.getModels().length === 0 && blazorMonaco.editors.length === 0",
            null,
            new() { Timeout = 30000 });
    }

    [Fact]
    public async Task WhenAWidgetIsCreatedInTheTextAreaOfAPhone_ThenItsBlockIsWrittenBeforeTheLineOfTheCursor()
    {
        // Arrange
        const string source = "# Scratch\n\nFirst line\n\nSecond line\n";
        await _fixture.WriteDemoPageAsync(ScratchPage, source);
        var page = await _fixture.CreatePageAsync(
            new BrowserNewContextOptions { HasTouch = true, IsMobile = true, ViewportSize = new() { Width = 375, Height = 667 } });
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{ScratchPage}");
        await page.WaitUntilInteractiveAsync();
        await page.SelectEditModeAsync("Source");

        var textArea = page.GetByTestId("markdown-source-text");
        await Assertions.Expect(textArea).ToHaveValueAsync(source, new() { Timeout = 30000 });
        await textArea.EvaluateAsync(
            "(element, offset) => { element.focus(); element.setSelectionRange(offset, offset); }",
            source.IndexOf("Second line", StringComparison.Ordinal) + 3);

        // Act
        await page.GetByTestId("create-subject-button").TapAsync();
        await page.GetByTestId("type-group-dashboard").TapAsync();
        var tileRow = SubjectSetupDialogTests.TypeRows(page).Filter(new() { HasText = "Value tile" });
        await tileRow.TapAsync();
        var dialog = SubjectSetupDialogTests.Dialog(page);
        await dialog.GetByLabel("Path", new() { Exact = true }).FillAsync("scratchmotor/CurrentSpeed");
        var createButton = SubjectSetupDialogTests.CreateButton(page);
        await Assertions.Expect(createButton).ToBeEnabledAsync(new() { Timeout = 30000 });
        await createButton.TapAsync();

        // Assert
        await Assertions.Expect(dialog).ToBeHiddenAsync(new() { Timeout = 30000 });
        var written = await _fixture.WaitForDemoPageTextAsync(
            ScratchPage, text => text.Contains(":: HomeBlaze.Components.ValueTile"), "The block of the widget was not written.");
        var lines = written.Split('\n');
        var headLineIndex = Array.FindIndex(lines, line => line.StartsWith(":: HomeBlaze.Components.ValueTile", StringComparison.Ordinal));
        Assert.True(headLineIndex > Array.IndexOf(lines, "First line"), written);
        Assert.True(headLineIndex < Array.IndexOf(lines, "Second line"), written);
        await Assertions.Expect(textArea).ToHaveValueAsync(written);
    }

    [Fact]
    public async Task WhenTheEditModeChangesBetweenAllModes_ThenTheCircuitStaysAndTheEditorWorksAfterEachChange()
    {
        // Arrange
        var page = await OpenWidgetsDemoPageAsync();
        var splitter = page.GetByTestId("edit-splitter");
        var monacoEditor = page.Locator(".monaco-editor");

        // Act & Assert: Split to Source once ended the circuit, because both modes had an outlet of the edit actions.
        foreach (var mode in new[] { "Split", "Source", "Split", "Inline", "Source", "View" })
        {
            await page.SelectEditModeAsync(mode);

            if (mode is "Split" or "Source")
            {
                await Assertions.Expect(splitter).ToHaveCountAsync(mode == "Split" ? 1 : 0, new() { Timeout = 30000 });
                await Assertions.Expect(monacoEditor).ToBeVisibleAsync(new() { Timeout = 30000 });

                // A dialog that opens and closes needs the circuit.
                await page.GetByTestId("create-subject-button").ClickAsync();
                var dialog = page.GetByTestId("create-subject-dialog");
                await Assertions.Expect(dialog).ToBeVisibleAsync(new() { Timeout = 30000 });
                await page.GetByTestId("cancel-button").ClickAsync();
                await Assertions.Expect(dialog).ToHaveCountAsync(0, new() { Timeout = 30000 });
            }
            else
            {
                await Assertions.Expect(monacoEditor).ToHaveCountAsync(0, new() { Timeout = 30000 });
                await Assertions.Expect(splitter).ToHaveCountAsync(0);
                var selectableWidgets = page.Locator(".subject-component-wrapper.selectable");
                if (mode == "Inline")
                {
                    await Assertions.Expect(selectableWidgets.First).ToBeVisibleAsync(new() { Timeout = 30000 });
                }
                else
                {
                    await Assertions.Expect(selectableWidgets).ToHaveCountAsync(0, new() { Timeout = 30000 });
                }
            }

            await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        }
    }

    [Theory]
    [InlineData(400, null)]
    [InlineData(768, null)]
    [InlineData(960, null)]
    [InlineData(1024, null)]
    [InlineData(1400, 80)]
    public async Task WhenTheEditorOfTheSplitModeIsNarrow_ThenThePageHeadStaysInTheEditorBarAndItsButtonsCanBeClicked(int width, int? previewPercent)
    {
        // Arrange
        var page = await OpenWidgetsDemoPageAsync(width);
        await page.SelectEditModeAsync("Split");
        await Assertions.Expect(page.Locator(".subject-block-decoration").First).ToBeAttachedAsync(new() { Timeout = 30000 });
        if (previewPercent is { } percent)
        {
            // Dragged from the middle, where the splitter starts, to the share of the preview.
            var splitter = await page.GetByTestId("edit-splitter").BoundingBoxAsync() ?? throw new InvalidOperationException("The splitter has no box");
            // A double, because Playwright passes a float to the page as an object.
            double previewWidth = (await page.GetByTestId("edit-splitter").Locator(".page-split-preview").BoundingBoxAsync())!.Width;
            var y = splitter.Y + 300;
            await page.Mouse.MoveAsync(splitter.X + splitter.Width / 2, y);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(splitter.X + splitter.Width * percent / 100, y, new() { Steps = 10 });
            await page.Mouse.UpAsync();
            await page.WaitForFunctionAsync(
                "minimum => document.querySelector('[data-testid=edit-splitter] .page-split-preview').getBoundingClientRect().width > minimum",
                previewWidth + 100,
                new() { Timeout = 10000 });
        }

        // Act: the cursor in the motor block adds its edit button, which makes the head as wide as it gets. It is placed
        // through the editor, because a narrow editor wraps the block out of view.
        await page.EvaluateAsync(
            """
            () => {
                const editor = blazorMonaco.editor.getEditor('markdown-editor');
                const line = editor.getModel().getLinesContent().findIndex(text => text.startsWith(':: HomeBlaze.Samples.Motor')) + 1;
                editor.focus();
                editor.setPosition({ lineNumber: line, column: 4 });
            }
            """);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Edit mymotor" })).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Assert: the head follows the width of the bar a moment after it changed. The bottoms agree only while the head
        // is placed with the margin that MudSplitter gives its content.
        const string headInBarScript =
            """
            () => {
                const head = document.querySelector('[data-testid=page-head]').getBoundingClientRect();
                const bar = document.querySelector('[data-testid=edit-splitter] [data-testid=editor-bar]').getBoundingClientRect();
                const isInside = head.left >= bar.left - 0.5 && head.right <= bar.right + 0.5 && Math.abs(head.bottom - bar.bottom) <= 1;
                return { isInside, text: `head ${head.left}-${head.right} bottom ${head.bottom}, bar ${bar.left}-${bar.right} bottom ${bar.bottom}` };
            }
            """;
        try
        {
            await page.WaitForFunctionAsync($"() => ({headInBarScript})().isInside", null, new() { Timeout = 10000 });
        }
        catch (TimeoutException)
        {
            var boxes = await page.EvaluateAsync<JsonElement>(headInBarScript);
            Assert.Fail($"The page head is not inside the editor bar: {boxes.GetProperty("text").GetString()}");
        }

        // Not forced, so that the click fails when another element covers the button.
        await page.GetByTestId("create-subject-button").ClickAsync(new() { Timeout = 10000 });
        await Assertions.Expect(page.GetByTestId("create-subject-dialog")).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    private async Task<IPage> OpenWidgetsDemoPageAsync(int? width = null)
    {
        var page = await _fixture.CreatePageAsync();

        // Before the page opens: a narrower window closes the navigation drawer with a transition.
        if (width is { } viewportWidth)
        {
            await page.SetViewportSizeAsync(viewportWidth, 900);
        }

        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/Inline.md");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // The edit menu is prerendered, and a click before the circuit is connected is lost.
        await page.WaitUntilInteractiveAsync();
        return page;
    }

    [Fact]
    public async Task WhenTheFormOfThePageEditorIsInvalid_ThenSaveIsDisabledUntilItIsCorrected()
    {
        // Arrange - the subject has no page component, but the page editor shows the generated properties
        // editor for it all the same.
        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{name}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            // The edit menu is prerendered, and a click before the circuit is connected is lost.
            await page.WaitUntilInteractiveAsync();

            await page.SelectEditModeAsync("Split");

            var saveButton = page.Locator("[data-testid='save-button'] button");
            var limitInput = page.GetByLabel("Limit");
            await Assertions.Expect(page.GetByLabel("Label", new() { Exact = true }))
                .ToHaveValueAsync("Original label", new() { Timeout = 30000 });

            // Assert - the subject is valid, so nothing has to be touched before it can be saved.
            await Assertions.Expect(saveButton).ToBeEnabledAsync(new() { Timeout = 5000 });

            // Act - Enter commits the text without leaving the field.
            await limitInput.FillAsync("1.5");
            await limitInput.PressAsync("Enter");

            // Assert
            await Assertions.Expect(saveButton).ToBeDisabledAsync(new() { Timeout = 5000 });
            Assert.Null(subject.Limit);

            // Act
            await limitInput.FillAsync("3");
            await limitInput.PressAsync("Enter");

            // Assert
            await Assertions.Expect(saveButton).ToBeEnabledAsync(new() { Timeout = 5000 });

            // Act
            await saveButton.ClickAsync();

            // Assert - the editor stays open after a save, and what it applied itself is no external change.
            // The subject has no file of its own, so nothing is written and the notification says so.
            await Assertions.Expect(page.GetByText("Configuration applied but not saved")).ToBeVisibleAsync(new() { Timeout = 5000 });
            Assert.Equal(3, subject.Limit);
            await Assertions.Expect(page.GetByText("Configuration was changed externally")).ToHaveCountAsync(0);
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }
}
