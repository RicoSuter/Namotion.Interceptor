using System.Text.Json;
using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Authorization;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Connects the MCP tools to the <see cref="AuthorizationService"/>.
/// </summary>
internal static class McpAuthorization
{
    /// <summary>
    /// The sentence that ends the text of a write, execute or history refusal of the access policy in a tool result, in
    /// the MCP library's set_property as in the application's tools; agents recognize refusals by it to log them.
    /// </summary>
    public const string RefusalSentence = "The access policy does not allow it.";

    /// <summary>
    /// The path provider the authorization matches rule paths with: one like that of the tools, so that a rule path
    /// matches the paths the tools show.
    /// </summary>
    public static StateAttributePathProvider PathProvider { get; } = new();

    /// <summary>
    /// Gets the access policy of a client holding <paramref name="roleNames"/> from the <see cref="AuthorizationService"/>;
    /// <see cref="AuthorizationPolicy.DenyAll"/> when the roles do not compile.
    /// </summary>
    public static AuthorizationMcpAccessPolicy GetAccessPolicy(IServiceProvider serviceProvider, IReadOnlyCollection<string> roleNames)
        => new(serviceProvider.GetRequiredService<AuthorizationService>().GetPolicy(roleNames).Policy);

    /// <summary>
    /// Gets the texts of a tool result that contain <see cref="RefusalSentence"/>: the lines of a text result, or the
    /// string values of any other result serialized as JSON.
    /// </summary>
    public static List<string> FindRefusals(object? result)
    {
        var refusals = new List<string>();
        if (result is string text)
        {
            foreach (var line in text.Split('\n'))
            {
                if (line.Contains(RefusalSentence, StringComparison.Ordinal))
                {
                    refusals.Add(line.Trim());
                }
            }
        }
        else if (result is not null)
        {
            // Tool results are open-ended objects, so they are serialized without generated metadata.
            AddRefusals(JsonSerializer.SerializeToElement(result), refusals);
        }

        return refusals;
    }

    private static void AddRefusals(JsonElement element, List<string> refusals)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (element.GetString() is { } value && value.Contains(RefusalSentence, StringComparison.Ordinal))
                {
                    refusals.Add(value);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AddRefusals(property.Value, refusals);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AddRefusals(item, refusals);
                }

                break;
        }
    }
}
