namespace Namotion.Matter;

/// <summary>The kind of a <see cref="MatterValue"/>.</summary>
public enum MatterValueKind
{
    /// <summary>A null value, also an absent one.</summary>
    Null,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>An integer or floating point number, also an enum or bitmap value.</summary>
    Number,

    /// <summary>A character string, or an octet string read with <see cref="MatterValue.GetBytes"/>.</summary>
    String,

    /// <summary>A list.</summary>
    List,

    /// <summary>A struct.</summary>
    Struct
}
