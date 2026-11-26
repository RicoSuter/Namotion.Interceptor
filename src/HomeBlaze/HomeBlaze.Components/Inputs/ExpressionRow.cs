namespace HomeBlaze.Components.Inputs;

/// <summary>
/// One condition of a simple expression: a path, a comparison and a value, joined to the previous row with
/// <see cref="Combinator"/>, which is empty for the first row.
/// </summary>
internal sealed record ExpressionRow(string Combinator, string Path, string Operator, string Value)
{
    public static ExpressionRow Empty(string combinator) => new(combinator, string.Empty, "==", string.Empty);

    /// <summary>The row as a condition of its own, such as <c>/Weather/WindGust &gt; 8</c>.</summary>
    public string ToCondition() => $"{Path} {Operator} {Value}";
}
