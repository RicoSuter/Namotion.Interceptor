namespace HomeBlaze.Components.Pages;

/// <summary>How a page is shown: viewed, or edited in one of the editing modes.</summary>
internal enum PageEditMode
{
    View, // Read-only rendered page
    Split, // Page on left, Monaco editor on right
    Source, // Full Monaco editor only
    Inline // View with inline editing on widgets
}

/// <summary>
/// The edit mode last chosen in the menu of a page, which every page that can be edited opens in for the rest of the
/// circuit. Scoped, so each browser tab has its own.
/// </summary>
internal sealed class PageEditModeChoice
{
    public PageEditMode Mode { get; set; } = PageEditMode.View;
}
