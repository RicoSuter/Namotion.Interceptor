using YamlDotNet.Serialization;

namespace HomeBlaze.Storage.Internal;

/// <summary>
/// Represents parsed YAML frontmatter from a markdown file.
/// </summary>
public class MarkdownFrontmatter
{
    [YamlMember(Alias = "title")]
    public string? Title { get; set; }

    [YamlMember(Alias = "navTitle")]
    public string? NavTitle { get; set; }

    [YamlMember(Alias = "order")]
    public int? Order { get; set; }

    [YamlMember(Alias = "icon")]
    public string? Icon { get; set; }
}
