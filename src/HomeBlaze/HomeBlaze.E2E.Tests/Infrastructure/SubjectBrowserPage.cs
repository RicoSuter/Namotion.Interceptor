namespace HomeBlaze.E2E.Tests.Infrastructure;

/// <summary>
/// Scripts for the subject browser page.
/// </summary>
public static class SubjectBrowserPage
{
    /// <summary>
    /// Resolves once the browser is interactive. The page is prerendered, and a click before its circuit is
    /// interactive is lost. The pane width is set by the script the interactive browser runs, so it marks the point
    /// from which clicks arrive.
    /// </summary>
    public const string IsInteractive =
        "() => (document.getElementById('scrollContainer')?.style.getPropertyValue('--pane-width') ?? '') !== ''";
}
