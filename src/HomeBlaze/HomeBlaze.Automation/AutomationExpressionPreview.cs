namespace HomeBlaze.Automation;

/// <summary>What <see cref="AutomationPreview"/> found for an expression or a template with the current values.</summary>
public sealed record AutomationExpressionPreview
{
    /// <summary>Why the text cannot be evaluated; the other members are empty then.</summary>
    public string? Error { get; init; }

    /// <summary>The expression as a condition: true, false or null for unknown. Null for a template.</summary>
    public bool? Result { get; init; }

    /// <summary>The value of the expression, or the rendered template; null when the value is missing.</summary>
    public object? Value { get; init; }

    /// <summary>The value as pages show it, or the rendered template; null when the value is missing.</summary>
    public string? Text { get; init; }

    /// <summary>The paths that led to no value.</summary>
    public IReadOnlyList<string> MissingPaths { get; init; } = [];
}
