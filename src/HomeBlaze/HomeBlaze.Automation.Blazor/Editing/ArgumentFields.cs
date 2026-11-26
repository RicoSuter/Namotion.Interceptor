using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Components.Dialogs;
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
    /// The arguments to keep for the operation: those whose key <see cref="AutomationValidator.IsArgumentKey"/> accepts, even
    /// when the editor shows the parameter as one field. All of them while the operation is not resolved (null).
    /// </summary>
    public static Dictionary<string, string> KeepKnown(MethodMetadata? method, IReadOnlyDictionary<string, string> arguments) =>
        arguments
            .Where(argument => method is null || AutomationValidator.IsArgumentKey(method, argument.Key))
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

        if (IsText(type))
        {
            return expressions.ValidateTemplate(text);
        }

        return ParameterConverter.TryConvert(text, type, out _) ? null : expressions.Validate(text);
    }

    /// <summary>Whether the type is a string or a nullable string, whose inputs are templates.</summary>
    public static bool IsText(Type? type) => type is not null && (Nullable.GetUnderlyingType(type) ?? type) == typeof(string);

    private static IEnumerable<ArgumentField> CreateFields(MethodParameter parameter)
    {
        if (OperationInputLayout.IsEnteredPerMember(parameter))
        {
            return ParameterConverter.GetObjectMembers(parameter.Type)
                .Select(member => CreateField(OperationInputLayout.GetMemberKey(parameter.Name, member.Name), member.Name, member.Type, member.IsRequired));
        }

        return [CreateField(parameter.Name, parameter.Name, parameter.Type, !parameter.IsNullable)];
    }

    private static ArgumentField CreateField(string key, string name, Type type, bool isRequired)
    {
        var valueType = Nullable.GetUnderlyingType(type) ?? type;
        var choices = valueType == typeof(bool) ? BooleanChoices : OperationInputLayout.GetEnumNames(valueType);
        return new ArgumentField(key, OperationInputLayout.GetLabel(name), type, isRequired, valueType == typeof(string), choices);
    }
}
