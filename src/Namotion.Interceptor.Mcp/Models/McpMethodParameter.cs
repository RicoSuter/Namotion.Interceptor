using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Namotion.Interceptor;
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
    /// Whether a member of an object argument must be given.
    /// </summary>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsRequired { get; init; }

    /// <summary>
    /// The members of an object argument, or null for other types and for types nested too deeply or recursively.
    /// </summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<McpMethodParameter>? Properties { get; init; }

    private const int MaximumPropertyDepth = 3;

    /// <summary>
    /// Describes a parameter of the given CLR type. A <see cref="Nullable{T}"/> type is always nullable. An object type
    /// lists its public settable members, by their JSON name in camel case, each with the text of its
    /// <see cref="DescriptionAttribute"/> as its description.
    /// </summary>
    /// <param name="name">The parameter name.</param>
    /// <param name="type">The CLR parameter type.</param>
    /// <param name="isNullable">Whether the argument may be null, for example a nullable reference type.</param>
    /// <param name="description">A hint on the meaning of the value, such as its unit or range.</param>
    public static McpMethodParameter Create(string name, Type type, bool isNullable = false, string? description = null) =>
        Create(name, type, isNullable, isRequired: false, description, depth: 0, visitedTypes: null);

    private static McpMethodParameter Create(
        string name, Type type, bool isNullable, bool isRequired, string? description, int depth, HashSet<Type>? visitedTypes)
    {
        var underlyingType = Nullable.GetUnderlyingType(type);
        var valueType = underlyingType ?? type;
        var schemaType = JsonSchemaTypeMapper.ToJsonSchemaType(valueType) ?? "object";
        return new McpMethodParameter
        {
            Name = name,
            Type = schemaType,
            Format = JsonSchemaTypeMapper.GetFormat(valueType),
            EnumValues = valueType.IsEnum ? Enum.GetNames(valueType) : null,
            IsNullable = isNullable || underlyingType is not null,
            IsRequired = isRequired,
            Description = description,
            Properties = schemaType == "object" ? CreateProperties(valueType, depth, visitedTypes) : null
        };
    }

    // Weak, so that the types of an unloaded plugin are not kept alive. Only depth 0 is cached: deeper members
    // depend on their ancestors in visitedTypes.
    private static readonly ConditionalWeakTable<Type, StrongBox<McpMethodParameter[]?>> PropertiesByTypeAtDepthZero = new();

    private static McpMethodParameter[]? CreateProperties(Type type, int depth, HashSet<Type>? visitedTypes)
    {
        if (depth > 0)
        {
            return CreatePropertiesCore(type, depth, visitedTypes!);
        }

        return PropertiesByTypeAtDepthZero
            .GetValue(type, static cachedType => new StrongBox<McpMethodParameter[]?>(CreatePropertiesCore(cachedType, 0, [])))
            .Value;
    }

    private static McpMethodParameter[]? CreatePropertiesCore(Type type, int depth, HashSet<Type> visitedTypes)
    {
        if (type == typeof(object) || type.IsAbstract || type.IsInterface || depth >= MaximumPropertyDepth
            || typeof(System.Collections.IEnumerable).IsAssignableFrom(type) || typeof(IInterceptorSubject).IsAssignableFrom(type)
            || !visitedTypes.Add(type))
        {
            return null;
        }

        try
        {
            // Owned by this call: the context caches the members it reads and is not thread-safe.
            var nullabilityContext = new NullabilityInfoContext();
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod?.IsPublic == true && property.SetMethod?.IsPublic == true &&
                                   property.GetIndexParameters().Length == 0 &&
                                   property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
                // Keep these member rules in sync with the host's member walker that builds arguments from form input.
                .GroupBy(property => property.Name)
                .Select(group => group.Aggregate((best, next) => next.DeclaringType!.IsSubclassOf(best.DeclaringType!) ? next : best))
                .Select(property => Create(
                    property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                    property.PropertyType,
                    nullabilityContext.Create(property).WriteState == NullabilityState.Nullable,
                    property.IsDefined(typeof(RequiredMemberAttribute)),
                    property.GetCustomAttribute<DescriptionAttribute>()?.Description,
                    depth + 1,
                    visitedTypes))
                .ToArray();

            return properties.Length > 0 ? properties : null;
        }
        finally
        {
            visitedTypes.Remove(type);
        }
    }
}
