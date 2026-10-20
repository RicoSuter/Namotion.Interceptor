using System.Runtime.CompilerServices;

namespace Namotion.Interceptor.Mcp.Tools;

/// <summary>
/// The interfaces of a type, read once per type, as <see cref="Type.GetInterfaces"/> allocates a new array per call.
/// </summary>
internal static class TypeInterfaces
{
    // Weak, so that a type of an assembly that is unloaded is not kept.
    private static readonly ConditionalWeakTable<Type, Type[]> InterfacesByType = new();

    /// <summary>Gets the interfaces of the type. The array is shared and must not be changed.</summary>
    public static Type[] Get(Type type) => InterfacesByType.GetValue(type, static type => type.GetInterfaces());
}
