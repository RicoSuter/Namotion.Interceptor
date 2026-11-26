using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.E2E.Tests.Models;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for what saving an edited page writes and applies.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PageSaveTests
{
    private const string EditablePageName = "Editable.md";
    private const string PagePath = "Demo/" + EditablePageName;
    private const string CardsPageName = "Cards.md";

    // Far enough in the past that any write of the file moves it, whatever the resolution of the file system.
    private static readonly DateTime UntouchedTime = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // A beforeunload that is canceled is the prompt of the browser, which Blazor gives while the page has unsaved text.
    private const string AsksBeforeUnload =
        "() => { const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event); return event.defaultPrevented; }";

    private readonly PlaywrightFixture _fixture;

    public PageSaveTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenMarkdownPageIsSaved_ThenOnlyThePageIsWritten()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var pageFilePath = GetPageFilePath(rootManager);
        var originalContent = await _fixture.ReadFileAsync(pageFilePath);
        var originalConfigurationTime = File.GetLastWriteTimeUtc(rootManager.ConfigurationPath);
        var pageSubject = GetPageSubject(rootManager);
        var marker = $"Saved {Guid.NewGuid():N}";

        try
        {
            File.SetLastWriteTimeUtc(rootManager.ConfigurationPath, UntouchedTime);

            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitUntilInteractiveAsync();

            await page.SelectEditModeAsync("Source");

            var editorContent = page.Locator(".monaco-editor .view-lines");
            await Assertions.Expect(editorContent).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editorContent.ClickAsync();
            await page.Keyboard.PressAsync("ControlOrMeta+End");
            await page.Keyboard.TypeAsync(marker);
            await Assertions.Expect(editorContent).ToContainTextAsync(marker, new() { Timeout = 30000 });

            // Act
            await page.Locator("[data-testid='save-button'] button").ClickAsync();

            // Assert: the notification is the last step of the save, so everything it writes is written by now.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });

            var savedContent = await _fixture.ReadFileAsync(pageFilePath);
            Assert.Multiple(
                () => Assert.Contains(marker, savedContent),
                () => Assert.Equal(UntouchedTime, File.GetLastWriteTimeUtc(rootManager.ConfigurationPath)),
                () => Assert.Same(pageSubject, GetPageSubject(rootManager)));
        }
        finally
        {
            await RestoreAsync(rootManager, originalContent, originalConfigurationTime);
        }
    }

    [Theory]
    [InlineData("Control+s")]
    [InlineData("Meta+s")]
    public async Task WhenTheSaveShortcutIsPressedInTheSourceEditor_ThenThePageIsWritten(string shortcut)
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var pageFilePath = GetPageFilePath(rootManager);
        var originalContent = await _fixture.ReadFileAsync(pageFilePath);
        var originalConfigurationTime = File.GetLastWriteTimeUtc(rootManager.ConfigurationPath);
        var marker = $"Saved {Guid.NewGuid():N}";

        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitUntilInteractiveAsync();

            await page.SelectEditModeAsync("Source");

            var editorContent = page.Locator(".monaco-editor .view-lines");
            await Assertions.Expect(editorContent).ToBeVisibleAsync(new() { Timeout = 30000 });
            await editorContent.ClickAsync();
            await page.Keyboard.PressAsync("ControlOrMeta+End");
            await page.Keyboard.TypeAsync(marker);
            await Assertions.Expect(editorContent).ToContainTextAsync(marker, new() { Timeout = 30000 });

            // Act
            await page.Keyboard.PressAsync(shortcut);

            // Assert
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.Contains(marker, await _fixture.ReadFileAsync(pageFilePath));
        }
        finally
        {
            await RestoreAsync(rootManager, originalContent, originalConfigurationTime);
        }
    }

    [Fact]
    public async Task WhenTheSourceEditorIsShown_ThenTheSaveButtonNamesTheShortcutOfThePlatform()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
        await page.WaitUntilInteractiveAsync();
        var isApplePlatform = await page.EvaluateAsync<bool>(
            "() => /mac|iphone|ipad|ipod/i.test(navigator.userAgentData?.platform ?? navigator.platform ?? '')");

        // Act
        await page.SelectEditModeAsync("Source");

        // Assert
        await Assertions.Expect(page.Locator("[data-testid='save-button'] button"))
            .ToHaveTextAsync(isApplePlatform ? "Save (Cmd+S)" : "Save (Ctrl+S)", new() { Timeout = 30000 });
    }

    [Fact]
    public async Task WhenAPageWithAPlaintextSecretIsSavedInTheSourceEditor_ThenTheEditorShowsTheWrittenReference()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var pageFilePath = GetPageFilePath(rootManager);
        var originalContent = await _fixture.ReadFileAsync(pageFilePath);
        var originalConfigurationTime = File.GetLastWriteTimeUtc(rootManager.ConfigurationPath);
        var secret = $"s3cr3t{Guid.NewGuid():N}";
        var block = $"\n\n:: {typeof(SecretConfigurableSubject).FullName}#vault Password={secret}\n::\n";

        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitUntilInteractiveAsync();

            await page.SelectEditModeAsync("Source");

            var editorContent = page.Locator(".monaco-editor .view-lines");
            await Assertions.Expect(editorContent).ToBeVisibleAsync(new() { Timeout = 30000 });
            await page.EvaluateAsync(
                "text => { const editor = monaco.editor.getEditors()[0]; const model = editor.getModel(); const line = model.getLineCount(); const column = model.getLineMaxColumn(line); editor.executeEdits('test', [{ range: new monaco.Range(line, column, line, column), text }]); }",
                block);
            await Assertions.Expect(editorContent).ToContainTextAsync(secret, new() { Timeout = 30000 });

            // Act
            await page.Locator("[data-testid='save-button'] button").ClickAsync();

            // Assert: the editor shows the written text, which is saved, so leaving the page asks nothing.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });
            await page.WaitForFunctionAsync(
                "secret => { const text = monaco.editor.getEditors()[0].getValue(); return text.includes('Password=$secret:') && !text.includes(secret); }",
                secret,
                new() { Timeout = 30000 });
            await page.WaitForFunctionAsync($"() => !({AsksBeforeUnload})()", null, new() { Timeout = 30000 });

            var editorText = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
            var savedContent = await _fixture.ReadFileAsync(pageFilePath);
            Assert.Multiple(
                () => Assert.DoesNotContain(secret, savedContent),
                () => Assert.Equal(savedContent.ReplaceLineEndings("\n"), editorText.ReplaceLineEndings("\n")));
        }
        finally
        {
            await RestoreAsync(rootManager, originalContent, originalConfigurationTime);
        }
    }

    [Fact]
    public async Task WhenAJsonFileWithAPlaintextSecretIsSavedInTheSourceEditor_ThenTheEditorShowsTheWrittenReference()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var root = (FluentStorageContainer)rootManager.Root!;
        var fileName = $"Plain{Guid.NewGuid():N}.json";
        var secret = $"s3cr3t{Guid.NewGuid():N}";
        var json = $$"""{ "$type": "{{typeof(SecretConfigurableSubject).FullName}}", "password": "{{secret}}" }""";

        try
        {
            await root.WriteBlobAsync(fileName, "{ \"Note\": \"plain\" }"u8.ToArray(), CancellationToken.None);
            await _fixture.ReconcileStorageAsync();
            var name = root.Children.Single(pair => pair.Value is JsonFile file && file.Name == fileName).Key;

            var page = await _fixture.OpenSubjectPageAsync(name);
            await page.SelectEditModeAsync("Source");

            var editorContent = page.Locator(".monaco-editor .view-lines");
            await Assertions.Expect(editorContent).ToBeVisibleAsync(new() { Timeout = 30000 });
            await page.EvaluateAsync(
                "text => { const editor = monaco.editor.getEditors()[0]; editor.executeEdits('test', [{ range: editor.getModel().getFullModelRange(), text }]); }",
                json);
            await Assertions.Expect(editorContent).ToContainTextAsync(secret, new() { Timeout = 30000 });

            // Act
            await page.Locator("[data-testid='save-button'] button").ClickAsync();

            // Assert: the editor shows the written text, which is saved, so leaving the page asks nothing.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });
            await page.WaitForFunctionAsync(
                "secret => { const text = monaco.editor.getEditors()[0].getValue(); return text.includes('$secret:') && !text.includes(secret); }",
                secret,
                new() { Timeout = 30000 });
            await page.WaitForFunctionAsync($"() => !({AsksBeforeUnload})()", null, new() { Timeout = 30000 });

            var editorText = await page.EvaluateAsync<string>("() => monaco.editor.getEditors()[0].getValue()");
            var savedContent = await _fixture.ReadFileAsync(_fixture.GetPagePath(fileName));
            Assert.Multiple(
                () => Assert.DoesNotContain(secret, savedContent),
                () => Assert.Equal(savedContent.ReplaceLineEndings("\n"), editorText.ReplaceLineEndings("\n")));
        }
        finally
        {
            await root.DeleteBlobAsync(fileName, CancellationToken.None);
        }
    }

    [Fact]
    public async Task WhenEmbeddedSubjectIsSaved_ThenItIsWrittenThroughItsPage()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var pageFilePath = GetPageFilePath(rootManager);
        var originalContent = await _fixture.ReadFileAsync(pageFilePath);
        var originalConfigurationTime = File.GetLastWriteTimeUtc(rootManager.ConfigurationPath);
        var pageSubject = GetPageSubject(rootManager);
        var motorName = $"Motor{Guid.NewGuid():N}";

        try
        {
            File.SetLastWriteTimeUtc(rootManager.ConfigurationPath, UntouchedTime);

            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/{PagePath}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitUntilInteractiveAsync();

            await page.SelectEditModeAsync("Inline");

            var actionBar = await page.Locator(".subject-component-wrapper.selectable:not(.text-block)").First.SelectWidgetAsync();
            await actionBar.GetByTestId("edit-subject-button").ClickAsync();

            var dialog = page.Locator(".mud-dialog");
            var nameField = dialog.GetByLabel("Name");
            await Assertions.Expect(nameField).ToBeVisibleAsync(new() { Timeout = 30000 });
            await nameField.FillAsync(motorName);

            // Act
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            // Assert: the notification is the last step of the save, so everything it writes is written by now.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });

            var savedContent = await _fixture.ReadFileAsync(pageFilePath);
            Assert.Multiple(
                () => Assert.Contains($":: HomeBlaze.Samples.Motor#editablemotor Name={motorName} TargetSpeed=3000", savedContent),
                () => Assert.Equal(UntouchedTime, File.GetLastWriteTimeUtc(rootManager.ConfigurationPath)),
                () => Assert.Same(pageSubject, GetPageSubject(rootManager)));
        }
        finally
        {
            await RestoreAsync(rootManager, originalContent, originalConfigurationTime);
        }
    }

    [Fact]
    public async Task WhenCardIsSavedInlineWithoutChanges_ThenItsPageIsNotChanged()
    {
        // Arrange
        var rootManager = _fixture.ServerServices.GetRequiredService<RootManager>();
        var pageFilePath = Path.Combine(rootManager.DataDirectory, "TestData", "Demo", CardsPageName);
        var originalContent = await _fixture.ReadFileAsync(pageFilePath);

        try
        {
            var page = await _fixture.CreatePageAsync();
            await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{CardsPageName}");
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitUntilInteractiveAsync();

            await page.SelectEditModeAsync("Inline");

            var actionBar = await page.GetByText("Sentence Card", new() { Exact = true }).SelectWidgetAsync();
            await actionBar.GetByTestId("edit-subject-button").ClickAsync();

            var dialog = page.Locator(".mud-dialog");
            await Assertions.Expect(dialog.GetByText("HomeBlaze.Components.Card")).ToBeVisibleAsync(new() { Timeout = 30000 });

            // Act
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

            // Assert: the notification is the last step of the save, so everything it writes is written by now.
            await Assertions.Expect(page.GetByText("Configuration saved.")).ToBeVisibleAsync(new() { Timeout = 30000 });
            Assert.Equal(originalContent, await _fixture.ReadFileAsync(pageFilePath));
        }
        finally
        {
            await _fixture.WriteDemoPageAsync(CardsPageName, originalContent);
        }
    }

    private static string GetPageFilePath(RootManager rootManager)
        => Path.Combine(rootManager.DataDirectory, "TestData", PagePath);

    private static MarkdownFile GetPageSubject(RootManager rootManager)
    {
        var root = (FluentStorageContainer)rootManager.Root!;
        var folder = (VirtualFolder)root.Children["Demo"];
        return (MarkdownFile)folder.Children[EditablePageName];
    }

    private async Task RestoreAsync(RootManager rootManager, string originalContent, DateTime originalConfigurationTime)
    {
        // Written through the subject, so that the page the server holds is restored along with the file.
        await _fixture.WriteDemoPageAsync(EditablePageName, originalContent);

        File.SetLastWriteTimeUtc(rootManager.ConfigurationPath, originalConfigurationTime);
    }
}
