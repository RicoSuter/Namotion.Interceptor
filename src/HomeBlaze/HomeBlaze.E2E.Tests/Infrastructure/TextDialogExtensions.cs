using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Finds the blocks of text of a page in inline edit mode and reads and writes the editor of the dialog that edits their text.
/// The editor is Monaco, which has no input value, so its text is read and written through the editor.
/// </summary>
internal static class TextDialogExtensions
{
    /// <summary>The id of the Monaco editor of the text dialog.</summary>
    public const string TextEditorId = "markdown-text-editor";

    private const int Timeout = 30000;

    /// <summary>
    /// Gets the wrapper of the block of text that contains the text.
    /// </summary>
    public static ILocator GetTextWrapper(this IPage page, string text) =>
        page.Locator(".subject-component-wrapper.selectable.text-block").Filter(new() { HasText = text });

    /// <summary>
    /// Waits until the editor of the text dialog has exactly the text, and fails with the text it has otherwise.
    /// </summary>
    public static async Task ExpectTextEditorValueAsync(this IPage page, string expected)
    {
        try
        {
            await page.WaitForFunctionAsync(
                $"expected => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true)?.getValue() === expected",
                expected,
                new() { Timeout = Timeout });
        }
        catch (TimeoutException)
        {
            var actual = await page.EvaluateAsync<string?>($"() => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true)?.getValue() ?? null");
            Assert.Fail($"The text editor has {(actual is null ? "not opened" : $"the text '{actual}'")} and not '{expected}'.");
        }
    }

    /// <summary>
    /// Waits until the text of the editor of the text dialog contains the text.
    /// </summary>
    public static Task ExpectTextEditorToContainAsync(this IPage page, string expected) =>
        page.WaitForFunctionAsync(
            $"expected => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true)?.getValue().includes(expected) === true",
            expected,
            new() { Timeout = Timeout });

    /// <summary>
    /// Waits until the editor of the text dialog has the keyboard focus.
    /// </summary>
    public static Task WaitForTextEditorFocusAsync(this IPage page) =>
        page.WaitForFunctionAsync(
            $"() => blazorMonaco.editor.getEditor('{TextEditorId}', true)?.hasTextFocus() === true", null, new() { Timeout = Timeout });

    /// <summary>
    /// Sets the text of the editor of the text dialog once it is open.
    /// </summary>
    public static async Task SetTextEditorValueAsync(this IPage page, string text)
    {
        await page.WaitForFunctionAsync(
            $"() => window.blazorMonaco?.editor.getEditor('{TextEditorId}', true) != null", null, new() { Timeout = Timeout });
        await page.EvaluateAsync($"text => blazorMonaco.editor.getEditor('{TextEditorId}').setValue(text)", text);
    }

    /// <summary>
    /// Puts the cursor of the editor of the text dialog at the start of the 1-based line.
    /// </summary>
    public static Task SetTextEditorCursorAsync(this IPage page, int lineNumber) =>
        page.EvaluateAsync($"line => blazorMonaco.editor.getEditor('{TextEditorId}').setPosition({{ lineNumber: line, column: 1 }})", lineNumber);
}
