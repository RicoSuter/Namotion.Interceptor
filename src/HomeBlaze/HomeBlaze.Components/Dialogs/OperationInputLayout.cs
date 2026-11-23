using HomeBlaze.Abstractions.Metadata;
using HomeBlaze.Services;

namespace HomeBlaze.Components.Dialogs;

/// <summary>How the operation dialog lays out the inputs of an operation, for other editors that ask for the same inputs.</summary>
public static class OperationInputLayout
{
    /// <summary>
    /// Whether the parameter is entered per member: a record parameter that is not nullable and that no single field can
    /// enter. Each member then has the key of <see cref="GetMemberKey"/>.
    /// </summary>
    public static bool IsEnteredPerMember(MethodParameter parameter) =>
        !ParameterConverter.IsSupported(parameter.Type) && !parameter.IsNullable && ParameterConverter.IsSupportedObject(parameter.Type);

    /// <summary>The key of the input of a member of a record parameter: <c>parameter.Member</c>.</summary>
    public static string GetMemberKey(string parameterName, string memberName) => $"{parameterName}.{memberName}";

    /// <summary>The name of a parameter or member as a label, with its first letter in upper case.</summary>
    public static string GetLabel(string name) => name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;

    /// <summary>The names of the members of an enum or nullable enum type, which the input offers as choices; null for any other type.</summary>
    public static string[]? GetEnumNames(Type type)
    {
        var valueType = Nullable.GetUnderlyingType(type) ?? type;
        return valueType.IsEnum ? Enum.GetNames(valueType) : null;
    }
}
