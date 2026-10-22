namespace HomeBlaze.Components;

/// <summary>
/// Marks the widget component of a block of text of a page, which can be selected while the page is edited inline although
/// its subject has no configuration. It gets the markup of the block as the additional parameter
/// <see cref="HtmlParameterName"/>, which also names the block for assistive technology.
/// </summary>
public interface ITextBlockWidget
{
    /// <summary>The name of the parameter that holds the markup of the block.</summary>
    const string HtmlParameterName = "Html";
}
