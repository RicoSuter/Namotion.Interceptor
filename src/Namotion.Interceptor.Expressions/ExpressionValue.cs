using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Interceptor.Expressions;

/// <summary>
/// The result of evaluating an expression or a part of it: a value, the property it was read from, and whether it is
/// already the text to show, as a formatting pipe gives it.
/// </summary>
public readonly struct ExpressionValue
{
    // Boxed once, so that a condition allocates nothing for its result.
    private static readonly object True = true;
    private static readonly object False = false;

    private ExpressionValue(object? value, RegisteredSubjectProperty? property, bool isText)
    {
        Value = value;
        Property = property;
        IsText = isText;
    }

    /// <summary>A path that leads nowhere, a null value, or an unknown condition.</summary>
    public static ExpressionValue Missing => default;

    /// <summary>The value, or null when it is missing. A string when <see cref="IsText"/> is true.</summary>
    public object? Value { get; }

    /// <summary>The property the value was read from, or null for a literal or a computed value.</summary>
    public RegisteredSubjectProperty? Property { get; }

    /// <summary>Whether <see cref="Value"/> is the text to show, which no further formatting changes.</summary>
    public bool IsText { get; }

    /// <summary>Whether there is no value.</summary>
    public bool IsMissing => Value is null;

    /// <summary>Creates the value of a property, which keeps the property for its unit and its source.</summary>
    public static ExpressionValue FromProperty(RegisteredSubjectProperty property, object? value) => new(value, property, false);

    /// <summary>Creates a value without a property.</summary>
    public static ExpressionValue FromValue(object? value) => new(value, null, false);

    /// <summary>Creates the final text of a formatting pipe.</summary>
    public static ExpressionValue FromText(string text) => new(text, null, true);

    /// <summary>Creates the result of a condition: true, false, or missing for unknown.</summary>
    public static ExpressionValue FromBoolean(bool? value) =>
        value is { } boolean ? new ExpressionValue(boolean ? True : False, null, false) : Missing;
}
