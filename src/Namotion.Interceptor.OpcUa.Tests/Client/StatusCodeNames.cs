using System.Reflection;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Client;

/// <summary>
/// Resolves a <see cref="StatusCodes"/> member by name. The SDK's status codes are static readonly values rather than
/// constants, so theories pass them by <c>nameof</c>.
/// </summary>
internal static class StatusCodeNames
{
    public static StatusCode Resolve(string name)
    {
        var field = typeof(StatusCodes).GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new ArgumentException($"Unknown status code '{name}'.", nameof(name));
        return (StatusCode)field.GetValue(null)!;
    }
}
