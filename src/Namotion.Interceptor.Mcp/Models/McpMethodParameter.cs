using System.Text.Json.Serialization;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Models;

/// <summary>
/// A method parameter as listed to an agent: its JSON Schema type and the hints needed to pass a valid argument.
/// </summary>
public sealed record McpMethodParameter
{
    /// <summary>
    /// The parameter name the argument is passed under.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// The JSON Schema type of the argument.
    /// </summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// The format label of a string argument, see <see cref="JsonSchemaTypeMapper.GetFormat"/>.
    /// </summary>
    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; init; }

    /// <summary>
    /// The allowed names of an enum argument.
    /// </summary>
    [JsonPropertyName("enum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? EnumValues { get; init; }

    /// <summary>
    /// Whether the argument may be null.
    /// </summary>
    [JsonPropertyName("nullable")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsNullable { get; init; }

    /// <summary>
    /// A host-supplied hint on the meaning of the value, such as its unit or range.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>
    /// Describes a parameter of the given CLR type. A <see cref="Nullable{T}"/> type is always nullable.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="type">The CLR parameter type.</param>
    /// <param name="isNullable">Whether the argument may be null, for example a nullable reference type.</param>
    /// <param name="description">A hint on the meaning of the value, such as its unit or range.</param>
    public static McpMethodParameter Create(string name, Type type, bool isNullable = false, string? description = null)
    {
        var underlyingType = Nullable.GetUnderlyingType(type);
        var valueType = underlyingType ?? type;
        return new McpMethodParameter
        {
            Name = name,
            Type = JsonSchemaTypeMapper.ToJsonSchemaType(valueType) ?? "object",
            Format = JsonSchemaTypeMapper.GetFormat(valueType),
            EnumValues = valueType.IsEnum ? Enum.GetNames(valueType) : null,
            IsNullable = isNullable || underlyingType is not null,
            Description = description
        };
    }
}
