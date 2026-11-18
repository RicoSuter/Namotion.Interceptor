using System.Text.RegularExpressions;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Replaces values that look like secrets by <c>***</c>, on a best effort basis: the password of a URL, the value
/// after a name such as <c>token</c>, <c>secret</c>, <c>password</c>, <c>api_key</c> or <c>auth</c> followed by
/// <c>=</c> or <c>:</c> (after the scheme of a value such as <c>Authorization: Basic</c>), and the token after
/// <c>Bearer</c>.
/// </summary>
internal static partial class RecentLogRedaction
{
    private const string Mask = "***";

    /// <summary>Returns the text with secret-looking values masked, or the same instance when there is none.</summary>
    public static string Redact(string text) => SecretPattern().Replace(text, "${prefix}" + Mask);

    [GeneratedRegex(
        """(?<prefix>://[^/\s:@]+:)[^/\s@]+(?=@|\u2026\z)""" +
        """|(?<prefix>(?:token|secret|passw(?:or)?d|pwd|api[-_]?key|auth)[\w-]*["']?\s*[=:]\s*["']?(?:(?:bearer|basic)\s+)?)[^\s&"',;]+""" +
        """|(?<prefix>\bbearer\s+)[\w\-.~+/]+=*""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture)]
    private static partial Regex SecretPattern();
}
