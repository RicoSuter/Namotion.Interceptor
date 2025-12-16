using System.Reflection;

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
    /// Whether this parameter type is supported for UI input (primitives, enums, nullable wrappers).
    /// </summary>
    public bool IsSupported { get; }

    /// <summary>
    /// The underlying ParameterInfo from reflection.
    /// </summary>
    public ParameterInfo ParameterInfo { get; }

    public SubjectMethodParameter(ParameterInfo parameterInfo, bool isSupported)
    {
        ParameterInfo = parameterInfo;
        Name = parameterInfo.Name ?? $"arg{parameterInfo.Position}";
        Type = parameterInfo.ParameterType;
        IsSupported = isSupported;
    }
}
