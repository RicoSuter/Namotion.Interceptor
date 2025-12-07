using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Parses YAML frontmatter from markdown content.
/// </summary>
public static class FrontmatterParser
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Parses YAML frontmatter from content that starts with --- delimiters.
    /// </summary>
    /// <typeparam name="T">The type to deserialize to.</typeparam>
    /// <param name="content">The content containing frontmatter.</param>
    /// <returns>The parsed frontmatter, or null if not found or invalid.</returns>
    public static T? Parse<T>(string content) where T : class
    {
        if (string.IsNullOrEmpty(content) || !content.StartsWith("---"))
            return null;

        var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return null;

        var yaml = content.Substring(3, endIndex - 3).Trim();
        if (string.IsNullOrWhiteSpace(yaml))
            return null;

        try
        {
            return YamlDeserializer.Deserialize<T>(yaml);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the content after the frontmatter section.
    /// </summary>
    public static string GetContentAfterFrontmatter(string content)
    {
        if (string.IsNullOrEmpty(content) || !content.StartsWith("---"))
            return content;

        var endIndex = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return content;

        return content.Substring(endIndex + 3).TrimStart();
    }
}
