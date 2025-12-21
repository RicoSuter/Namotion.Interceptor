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
    /// The underlying ParameterInfo from reflection.
    /// </summary>
    public ParameterInfo ParameterInfo { get; }

    public SubjectMethodParameter(ParameterInfo parameterInfo)
    {
        ParameterInfo = parameterInfo;
        Name = parameterInfo.Name ?? $"arg{parameterInfo.Position}";
        Type = parameterInfo.ParameterType;
    }
}
