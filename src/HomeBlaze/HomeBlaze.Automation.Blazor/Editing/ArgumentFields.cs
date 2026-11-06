using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// The argument fields of an operation, laid out as the operation dialog does: one field per parameter that needs input,
/// and one per member for a record parameter that is not nullable.
/// </summary>
internal static class ArgumentFields
{
    private const string RequiredError = "Required";

    private static readonly string[] BooleanChoices = ["true", "false"];

    public static IReadOnlyList<ArgumentField> Create(MethodMetadata method) =>
        [.. method.Parameters.Where(parameter => parameter.RequiresInput).SelectMany(CreateFields)];

    /// <summary>
    /// The arguments to keep when the operation changes: those whose key belongs to one of the fields, which the binder matches
    /// without regard to case, because it rejects any other key. All of them while the new operation is not resolved (null),
    /// such as while its path is being typed.
    /// </summary>
    public static Dictionary<string, string> KeepKnown(IReadOnlyList<ArgumentField>? fields, IReadOnlyDictionary<string, string> arguments) =>
        arguments
            .Where(argument => fields is null || fields.Any(field => string.Equals(field.Key, argument.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(argument => argument.Key, argument => argument.Value);

    /// <summary>
    /// The error of the text of an argument or value, or null. The text of a string is a template, any other text is a constant
    /// when it converts to the type and otherwise an expression. The type is null while the target is unknown.
    /// </summary>
    public static string? Validate(string? text, Type? type, bool isRequired, RuleExpressions expressions)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return isRequired ? RequiredError : null;
        }

        if (type is null)
        {
            return null;
        }

        if ((Nullable.GetUnderlyingType(type) ?? type) == typeof(string))
        {
            return expressions.ValidateTemplate(text);
        }

        return ParameterConverter.TryConvert(text, type, out _) ? null : expressions.Validate(text);
    }

    private static IEnumerable<ArgumentField> CreateFields(MethodParameter parameter)
    {
        if (!ParameterConverter.IsSupported(parameter.Type) && !parameter.IsNullable && ParameterConverter.IsSupportedObject(parameter.Type))
        {
            return ParameterConverter.GetObjectMembers(parameter.Type)
                .Select(member => CreateField($"{parameter.Name}.{member.Name}", member.Name, member.Type, member.IsRequired));
        }

        return [CreateField(parameter.Name, parameter.Name, parameter.Type, !parameter.IsNullable)];
    }

    private static ArgumentField CreateField(string key, string name, Type type, bool isRequired)
    {
        var valueType = Nullable.GetUnderlyingType(type) ?? type;
        return new ArgumentField(key, ToLabel(name), type, isRequired, valueType == typeof(string), GetChoices(valueType));
    }

    private static IReadOnlyList<string>? GetChoices(Type valueType)
    {
        if (valueType.IsEnum)
        {
            return Enum.GetNames(valueType);
        }

        return valueType == typeof(bool) ? BooleanChoices : null;
    }

    private static string ToLabel(string name) => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
}
