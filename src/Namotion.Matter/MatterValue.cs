using System.Globalization;
using System.Text.Json;

namespace Namotion.Matter;

/// <summary>
/// An attribute value, event payload or command response received from a node. The default value is null.
/// A getter that does not match <see cref="Kind"/> throws <see cref="InvalidOperationException"/>; a number getter whose
/// type cannot hold the number, and <see cref="GetBytes"/> for text that is not valid base64, throw
/// <see cref="FormatException"/>.
/// </summary>
/// <remarks>
/// Values are equal when their content is: struct fields in any order, and numbers by value. The hash code only
/// distinguishes the kind, so a value is a poor dictionary key.
/// </remarks>
public readonly struct MatterValue : IEquatable<MatterValue>
{
    private readonly JsonElement _element;

    internal MatterValue(JsonElement element) => _element = element;

    /// <summary>Gets the kind of the value.</summary>
    public MatterValueKind Kind => _element.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => MatterValueKind.Null,
        JsonValueKind.True or JsonValueKind.False => MatterValueKind.Boolean,
        JsonValueKind.Number => MatterValueKind.Number,
        JsonValueKind.String => MatterValueKind.String,
        JsonValueKind.Array => MatterValueKind.List,
        _ => MatterValueKind.Struct
    };

    /// <summary>Gets whether the value is null.</summary>
    public bool IsNull => Kind == MatterValueKind.Null;

    /// <summary>Gets the value as a boolean.</summary>
    public bool GetBoolean() => _element.GetBoolean();

    /// <summary>Gets the value as a signed integer.</summary>
    public long GetInt64() => _element.GetInt64();

    /// <summary>Gets the value as an unsigned integer.</summary>
    public ulong GetUInt64() => _element.GetUInt64();

    /// <summary>Gets the value as a floating point number.</summary>
    public double GetDouble() => _element.GetDouble();

    /// <summary>Gets the value as a character string.</summary>
    public string GetString() => _element.GetString() ?? throw new InvalidOperationException("The value is null.");

    /// <summary>Gets the value as an octet string.</summary>
    public byte[] GetBytes() => _element.GetBytesFromBase64();

    /// <summary>Gets the number of items of a list.</summary>
    public int GetListLength() => _element.GetArrayLength();

    /// <summary>Enumerates the items of a list.</summary>
    public ListEnumerator EnumerateList() => new(_element.EnumerateArray());

    /// <summary>
    /// Gets a field of a struct. Attribute values key fields by tag and event payloads and command responses by name,
    /// so the field is looked up by its tag first and by its name second.
    /// </summary>
    /// <param name="tag">The field's tag (ID).</param>
    /// <param name="name">The field's name as the server spells it, in lower camel case.</param>
    /// <param name="value">The field, or null when it is missing.</param>
    /// <returns>Whether the value is a struct that has the field.</returns>
    public bool TryGetField(uint tag, string name, out MatterValue value)
    {
        if (_element.ValueKind == JsonValueKind.Object)
        {
            Span<char> tagText = stackalloc char[10];
            tag.TryFormat(tagText, out var written, provider: CultureInfo.InvariantCulture);
            if (_element.TryGetProperty(tagText[..written], out var field) ||
                _element.TryGetProperty(name, out field))
            {
                value = new MatterValue(field);
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Compares two values by content.</summary>
    public static bool operator ==(MatterValue left, MatterValue right) => left.Equals(right);

    /// <summary>Compares two values by content.</summary>
    public static bool operator !=(MatterValue left, MatterValue right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(MatterValue other) => IsNull || other.IsNull
        ? IsNull && other.IsNull
        : JsonElement.DeepEquals(_element, other._element);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MatterValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)Kind;

    /// <summary>Returns the value's text form for logging.</summary>
    public override string ToString() => _element.ValueKind == JsonValueKind.Undefined ? "null" : _element.GetRawText();

    /// <summary>Enumerates the items of a list value without allocating.</summary>
    public struct ListEnumerator
    {
        private JsonElement.ArrayEnumerator _inner;

        internal ListEnumerator(JsonElement.ArrayEnumerator inner) => _inner = inner;

        /// <summary>Gets the current item.</summary>
        public readonly MatterValue Current => new(_inner.Current);

        /// <summary>Moves to the next item.</summary>
        public bool MoveNext() => _inner.MoveNext();

        /// <summary>Returns this enumerator, so it can be used in a <c>foreach</c> loop.</summary>
        public readonly ListEnumerator GetEnumerator() => this;
    }
}
