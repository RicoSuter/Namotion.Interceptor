using System.Reflection;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Services;

/// <summary>
/// Describes a method marked with [Operation] or [Query] attribute.
/// </summary>
public sealed class SubjectMethodInfo
{
    /// <summary>
    /// The underlying MethodInfo from reflection.
    /// </summary>
    public MethodInfo MethodInfo { get; }

    /// <summary>
    /// Display name (Title from attribute, or method name).
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Description shown as tooltip in UI.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Icon name (e.g., "PlayArrow"), same convention as IIconProvider.IconName.
    /// </summary>
    public string? Icon { get; }

    /// <summary>
    /// Sort position in UI.
    /// </summary>
    public int Position { get; }

    /// <summary>
    /// Whether this is an Operation or Query.
    /// </summary>
    public SubjectMethodKind Kind { get; }

    /// <summary>
    /// Whether a confirmation dialog should be shown before executing.
    /// Only applicable to Operations.
    /// </summary>
    public bool RequiresConfirmation { get; }

    /// <summary>
    /// Method parameters.
    /// </summary>
    public SubjectMethodParameter[] Parameters { get; }

    /// <summary>
    /// Unwrapped return type (T from Task{T}, null for void/Task).
    /// </summary>
    public Type? ResultType { get; }

    /// <summary>
    /// Whether the method returns Task or Task{T}.
    /// </summary>
    public bool IsAsync => typeof(Task).IsAssignableFrom(MethodInfo.ReturnType);

    /// <summary>
    /// Whether all parameters are supported for UI input.
    /// </summary>
    public bool IsSupported => Array.TrueForAll(Parameters, p => p.IsSupported);

    public SubjectMethodInfo(
        MethodInfo methodInfo,
        SubjectMethodAttribute attribute,
        SubjectMethodKind kind,
        SubjectMethodParameter[] parameters,
        Type? resultType)
    {
        MethodInfo = methodInfo;
        Title = attribute.Title ?? methodInfo.Name.Replace("Async", "");
        Description = attribute.Description;
        Icon = attribute.Icon;
        Position = attribute.Position;
        Kind = kind;
        RequiresConfirmation = attribute is OperationAttribute op && op.RequiresConfirmation;
        Parameters = parameters;
        ResultType = resultType;
    }
}
