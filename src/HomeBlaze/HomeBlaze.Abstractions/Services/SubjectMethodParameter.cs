using System.Reflection;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Services;

/// <summary>
/// Describes a parameter of a subject method.
/// </summary>
public sealed class SubjectMethodParameter
{
    /// <summary>
    /// Parameter name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Parameter type.
    /// </summary>
    public Type Type { get; }

    /// <summary>
    /// The underlying ParameterInfo from reflection.
    /// </summary>
    public ParameterInfo ParameterInfo { get; }

    /// <summary>
    /// Whether the parameter type is supported for UI rendering.
    /// </summary>
    public bool IsSupported { get; }

    /// <summary>
    /// The unit of the parameter value (from OperationParameterAttribute), or null if not specified.
    /// </summary>
    public StateUnit? Unit { get; }

    public SubjectMethodParameter(ParameterInfo parameterInfo, bool isSupported)
    {
        ParameterInfo = parameterInfo;
        Name = parameterInfo.Name ?? $"arg{parameterInfo.Position}";
        Type = parameterInfo.ParameterType;
        IsSupported = isSupported;

        var attribute = parameterInfo.GetCustomAttribute<OperationParameterAttribute>();
        Unit = attribute?.Unit;
    }
}
