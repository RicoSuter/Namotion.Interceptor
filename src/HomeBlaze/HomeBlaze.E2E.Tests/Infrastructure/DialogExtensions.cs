using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Waits for the dialogs of MudBlazor, which move the focus into themselves only after they show.
/// </summary>
internal static class DialogExtensions
{
    private const int Timeout = 30000;

    /// <summary>
    /// Waits until a dialog that has just opened has moved the focus into itself, which it does two round trips of the
    /// circuit after it renders. A fill or a key sent to a field of the dialog before then can lose its text or its key to
    /// the element that the dialog focuses. Call it before anything in the dialog is focused by the test itself.
    /// </summary>
    public static Task WaitForDialogFocusAsync(this ILocator dialog) =>
        Assertions.Expect(dialog.Locator(":focus")).ToHaveCountAsync(1, new() { Timeout = Timeout });
}
