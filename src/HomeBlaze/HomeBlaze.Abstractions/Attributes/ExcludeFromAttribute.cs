using System.Reflection;
using System.Runtime.CompilerServices;

namespace HomeBlaze.Abstractions.Attributes;

/// <summary>
/// Excludes a subject type, and the types derived from it, or an operation or query method from the given targets.
/// Without it a type or a method is offered everywhere. It decides where something is offered, not who may reach it:
/// a subject excluded from agents is still called by its path, and a path typed into a widget still runs a method
/// that is excluded from user interfaces.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class ExcludeFromAttribute(ExclusionTarget targets) : Attribute
{
    // Weak, so that the types of an unloaded plugin are not kept alive.
    private static readonly ConditionalWeakTable<Type, object> TargetsByType = new();

    /// <summary>
    /// Gets the targets that the type or the method is excluded from.
    /// </summary>
    public ExclusionTarget Targets { get; } = targets;

    /// <summary>
    /// Gets whether the type, or a type it derives from, is excluded from any of the given targets.
    /// </summary>
    public static bool IsExcluded(Type type, ExclusionTarget target)
    {
        var targets = (ExclusionTarget)TargetsByType.GetValue(type, static candidate =>
            candidate.GetCustomAttribute<ExcludeFromAttribute>(inherit: true)?.Targets ?? ExclusionTarget.None);
        return (targets & target) != 0;
    }
}
