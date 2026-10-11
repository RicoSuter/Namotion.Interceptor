using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Resolves the subject path a HomeBlaze MCP tool receives.
/// </summary>
internal static class McpSubjectPathResolver
{
    /// <summary>
    /// Returns the subject at <paramref name="path"/>, the root for an empty path or "/", or null when the path
    /// does not resolve or the root is not registered.
    /// </summary>
    public static RegisteredSubject? TryResolveSubject(
        Func<IInterceptorSubject> rootSubjectProvider, PathProviderBase pathProvider, string path)
    {
        var rootRegistered = rootSubjectProvider().TryGetRegisteredSubject();
        if (rootRegistered is null)
        {
            return null;
        }

        return string.IsNullOrEmpty(path) || path == "/"
            ? rootRegistered
            : pathProvider.TryGetSubjectFromPath(rootRegistered, path);
    }
}
