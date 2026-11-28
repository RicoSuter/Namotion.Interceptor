namespace Namotion.Matter;

/// <summary>
/// Writes a value sent to a node, such as a command payload or an attribute value. Each struct field is written as
/// <see cref="WriteFieldName"/> followed by its value; a backend uses the tag or the name, whichever its wire format needs.
/// </summary>
public interface IMatterValueWriter
{
    /// <summary>Writes a null value.</summary>
    void WriteNull();

    /// <summary>Writes a boolean.</summary>
    void WriteBoolean(bool value);

    /// <summary>Writes a signed integer.</summary>
    void WriteInt64(long value);

    /// <summary>Writes an unsigned integer, also an enum or bitmap value.</summary>
    void WriteUInt64(ulong value);

    /// <summary>Writes a floating point number.</summary>
    void WriteDouble(double value);

    /// <summary>Writes a character string.</summary>
    void WriteString(string value);

    /// <summary>Writes an octet string.</summary>
    void WriteBytes(ReadOnlySpan<byte> value);

    /// <summary>Starts a struct.</summary>
    void WriteStartStruct();

    /// <summary>Ends the current struct.</summary>
    void WriteEndStruct();

    /// <summary>Starts a list.</summary>
    void WriteStartList();

    /// <summary>Ends the current list.</summary>
    void WriteEndList();

    /// <summary>Writes the name of the next struct field.</summary>
    /// <param name="tag">The field's tag (ID).</param>
    /// <param name="name">The field's name as the server spells it, in lower camel case.</param>
    void WriteFieldName(uint tag, string name);
}
