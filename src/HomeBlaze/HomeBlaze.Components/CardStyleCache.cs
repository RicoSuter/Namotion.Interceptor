namespace HomeBlaze.Components;

/// <summary>
/// Keeps the style of one card for its last <see cref="Card.Height"/> and <see cref="Card.AspectRatio"/>, so that a
/// render with the same values does not build the style again. Not thread-safe: one per component.
/// </summary>
internal sealed class CardStyleCache
{
    private bool _hasStyle;
    private int? _height;
    private string? _aspectRatio;
    private string? _style;
    private string? _error;

    /// <summary>
    /// Gets the style of <see cref="CardSize.GetStyle"/>, built again only when the height or the aspect ratio changed.
    /// </summary>
    public string? GetStyle(int? height, string? aspectRatio, out string? error)
    {
        if (!_hasStyle || height != _height || !string.Equals(aspectRatio, _aspectRatio, StringComparison.Ordinal))
        {
            _style = CardSize.GetStyle(height, aspectRatio, out _error);
            _height = height;
            _aspectRatio = aspectRatio;
            _hasStyle = true;
        }

        error = _error;
        return _style;
    }
}
