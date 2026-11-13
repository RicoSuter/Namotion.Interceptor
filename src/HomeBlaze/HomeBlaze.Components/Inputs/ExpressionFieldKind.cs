namespace HomeBlaze.Components.Inputs;

/// <summary>What an <see cref="ExpressionField"/> edits.</summary>
public enum ExpressionFieldKind
{
    /// <summary>A condition, as simple rows or as text, whose result is true, false or unknown.</summary>
    Condition,

    /// <summary>A value, as text only, whose result is shown as the host formats it.</summary>
    Value
}
