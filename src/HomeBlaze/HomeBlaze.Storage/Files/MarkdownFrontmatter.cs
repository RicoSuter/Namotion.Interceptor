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

    /// <summary>
    /// Alternative snake_case mapping for nav_title.
    /// </summary>
    [YamlMember(Alias = "nav_title")]
    public string? NavTitleSnakeCase
    {
        get => NavTitle;
        set => NavTitle ??= value;
    }

    [YamlMember(Alias = "position")]
    public int? Position { get; set; }

    [YamlMember(Alias = "icon")]
    public string? Icon { get; set; }

    [YamlMember(Alias = "location")]
    public string? Location { get; set; }

    [YamlMember(Alias = "alignment")]
    public string? Alignment { get; set; }
}
