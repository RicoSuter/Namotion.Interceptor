using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;

namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// The argument fields of an operation, laid out as the operation dialog does: one field per parameter that needs input,
/// and one per member for a record parameter that is not nullable.
/// </summary>
internal static class ArgumentFields
{
    private static readonly string[] BooleanChoices = ["true", "false"];

    public static IReadOnlyList<ArgumentField> Create(MethodMetadata method) =>
        [.. method.Parameters.Where(parameter => parameter.RequiresInput).SelectMany(CreateFields)];

    /// <summary>The arguments whose key belongs to one of the fields, which the binder matches without regard to case; any other key is rejected at run time.</summary>
    public static Dictionary<string, string> KeepKnown(IReadOnlyList<ArgumentField> fields, IReadOnlyDictionary<string, string> arguments) =>
        arguments
            .Where(argument => fields.Any(field => string.Equals(field.Key, argument.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(argument => argument.Key, argument => argument.Value);

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
