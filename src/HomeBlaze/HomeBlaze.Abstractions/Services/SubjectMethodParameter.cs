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
    /// The unit of the parameter value (from OperationParameterAttribute), or null if not specified.
    /// </summary>
    public StateUnit? Unit { get; }

    /// <summary>
    /// Whether this parameter should be resolved from dependency injection.
    /// </summary>
    public bool IsFromServices { get; }

    public SubjectMethodParameter(ParameterInfo parameterInfo)
    {
        ParameterInfo = parameterInfo;
        Name = parameterInfo.Name ?? $"arg{parameterInfo.Position}";
        Type = parameterInfo.ParameterType;
        IsFromServices = parameterInfo.GetCustomAttribute<FromServicesAttribute>() != null;

        var attribute = parameterInfo.GetCustomAttribute<OperationParameterAttribute>();
        Unit = attribute?.Unit;
    }
}
