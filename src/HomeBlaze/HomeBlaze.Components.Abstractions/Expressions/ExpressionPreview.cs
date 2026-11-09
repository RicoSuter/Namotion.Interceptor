namespace HomeBlaze.Components.Inputs;

/// <summary>What an <see cref="ExpressionField"/> shows for an expression with the current values.</summary>
/// <param name="Result">The expression as a condition: true, false or null for unknown.</param>
/// <param name="Value">The value of the expression, or null when it is missing.</param>
/// <param name="Text">The value as the host shows it, or null when it is missing.</param>
public sealed record ExpressionPreview(bool? Result, object? Value, string? Text);
