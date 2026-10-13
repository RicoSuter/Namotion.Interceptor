namespace HomeBlaze.Storage.Pages;

/// <summary>How an expression on a page writes its value.</summary>
public enum ExpressionFormat
{
    /// <summary>The text the property panel shows: with the unit of the property, scaled and rounded.</summary>
    Display,

    /// <summary>The plain value, written the same on every system.</summary>
    Raw
}
