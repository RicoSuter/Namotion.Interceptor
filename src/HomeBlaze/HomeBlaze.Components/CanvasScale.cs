namespace HomeBlaze.Components;

/// <summary>How a <see cref="CanvasLayout"/> is sized on the page.</summary>
public enum CanvasScale
{
    /// <summary>Every node keeps its size and position in pixels.</summary>
    None,

    /// <summary>While viewed, the canvas is drawn at the factor of the available width to its design width.</summary>
    FitWidth
}
