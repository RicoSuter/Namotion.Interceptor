namespace Namotion.Interceptor.ConnectorTester.Reporting;

/// <summary>
/// Defines one column of a CsvFile&lt;TRow&gt;: header text, fixed width (right-aligned),
/// optional format string (e.g. "F1", "yyyy-MM-ddTHH:mm:ss.fffZ"), and a selector that
/// extracts the column value from a row instance.
/// </summary>
public sealed class CsvColumn<TRow>
{
    public string Name { get; init; } = "";
    public int Width { get; init; }
    public string? Format { get; init; }
    public Func<TRow, object?> Selector { get; init; } = _ => "";
}
