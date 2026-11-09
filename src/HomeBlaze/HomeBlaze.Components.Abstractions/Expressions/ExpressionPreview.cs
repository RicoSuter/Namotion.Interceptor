namespace HomeBlaze.Components.Abstractions.Expressions;

/// <summary>An expression evaluated with the current values, as an expression field shows it next to the expression.</summary>
/// <param name="Result">The expression as a condition: true, false or null for anything else or a missing value.</param>
/// <param name="Value">The value of the expression, or null when it is missing.</param>
/// <param name="Text">The value as the host shows it, why it cannot be read, or null when it is missing.</param>
public sealed record ExpressionPreview(bool? Result, object? Value, string? Text);
