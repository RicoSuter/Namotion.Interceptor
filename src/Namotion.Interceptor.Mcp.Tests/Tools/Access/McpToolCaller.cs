using System.Text.Json;
using Namotion.Interceptor.Mcp.Tools;

namespace Namotion.Interceptor.Mcp.Tests.Tools.Access;

internal static class McpToolCaller
{
    public static async Task<JsonElement> CallAsync(
        IInterceptorSubject root, McpServerConfiguration configuration, string toolName, object input)
    {
        var tool = new McpToolFactory(root, configuration).CreateTools().First(tool => tool.Name == toolName);
        var result = await tool.Handler(JsonSerializer.SerializeToElement(input), CancellationToken.None);
        return JsonSerializer.SerializeToElement(result);
    }

    /// <summary>
    /// The paths of the subjects a JSON search result lists, in result order.
    /// </summary>
    public static string[] GetResultPaths(JsonElement searchResult) =>
        searchResult.GetProperty("results").EnumerateObject().Select(entry => entry.Name).ToArray();
}
