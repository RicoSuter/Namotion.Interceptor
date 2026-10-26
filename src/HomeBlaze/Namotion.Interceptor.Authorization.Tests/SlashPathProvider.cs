using Namotion.Interceptor.Registry.Paths;

namespace Namotion.Interceptor.Authorization.Tests;

/// <summary>Slash-separated paths, as the MCP tools of an application use them.</summary>
public sealed class SlashPathProvider : PathProviderBase
{
    public static readonly SlashPathProvider Instance = new();

    public override char PathSeparator => '/';
}
