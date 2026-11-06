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
    /// The arguments to keep for the operation: those the binder accepts, which is a key equal to a parameter that needs input
    /// or <c>parameter.Member</c> for a parameter of a record type with that member, matched without regard to case, even
    /// when the editor shows the parameter as one field. The binder rejects any other key. All of them while the operation is
    /// not resolved (null).
    /// </summary>
    public static Dictionary<string, string> KeepKnown(MethodMetadata? method, IReadOnlyDictionary<string, string> arguments) =>
        arguments
            .Where(argument => method is null || IsAccepted(method, argument.Key))
            .ToDictionary(argument => argument.Key, argument => argument.Value);

    // Mirrors the key check of the argument binder of the runtime.
    private static bool IsAccepted(MethodMetadata method, string key)
    {
        var inputs = method.Parameters.Where(parameter => parameter.RequiresInput).ToList();
        if (inputs.Exists(parameter => IsSame(parameter.Name, key)))
        {
            return true;
        }

        var separator = key.IndexOf('.');
        var owner = separator < 0 ? null : inputs.Find(parameter => IsSame(parameter.Name, key[..separator]));
        return owner is not null && ParameterConverter.IsSupportedObject(owner.Type) &&
            ParameterConverter.GetObjectMembers(owner.Type).Any(member => IsSame(member.Name, key[(separator + 1)..]));
    }

    private static bool IsSame(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

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
