namespace HomeBlaze.Components.Controls;

/// <summary>
/// The widget that a control widget renders for its target.
/// </summary>
public enum ControlKind
{
    /// <summary>A row with the label and the formatted value, for every target that no other kind takes.</summary>
    Value,

    /// <summary>A button, for an operation or a query.</summary>
    OperationButton,

    /// <summary>A switch, for a boolean that can be written.</summary>
    Switch,

    /// <summary>A row with the label and an indicator, for a boolean that cannot be written.</summary>
    Indicator,

    /// <summary>A select, for an enum that can be written and is no flags enum.</summary>
    Select,

    /// <summary>A slider, for a number that can be written and has a range.</summary>
    Slider
}
