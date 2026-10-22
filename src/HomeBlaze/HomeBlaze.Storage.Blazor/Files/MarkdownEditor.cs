using BlazorMonaco.Editor;

namespace HomeBlaze.Storage.Blazor.Files;

/// <summary>The settings that every Monaco editor of Markdown shares, so that the page editor and the text dialog look alike.</summary>
internal static class MarkdownEditor
{
    /// <summary>The module that reads, decorates and focuses an editor of Markdown.</summary>
    public const string ModulePath = "./_content/HomeBlaze.Storage.Blazor/Files/MarkdownFileEditComponent.razor.js";

    /// <summary>Gets the Monaco theme of the color mode.</summary>
    public static string GetTheme(bool isDarkMode) => isDarkMode ? "vs-dark" : "vs";

    /// <summary>Creates the options of an editor that starts with the text.</summary>
    public static StandaloneEditorConstructionOptions CreateOptions(string text, bool isDarkMode)
    {
        return new StandaloneEditorConstructionOptions
        {
            AutomaticLayout = true,
            Language = "markdown",
            Value = text,
            WordWrap = "on",
            Theme = GetTheme(isDarkMode),
            Folding = true,
            ShowFoldingControls = "always",
            StickyScroll = new EditorStickyScrollOptions
            {
                Enabled = true,
                DefaultModel = "foldingProviderModel",
                MaxLineCount = 6
            }
        };
    }
}

/// <summary>The text of an editor of Markdown together with the version of its model, as the module gives it.</summary>
internal sealed record MarkdownEditorContent(int Version, string Text);
