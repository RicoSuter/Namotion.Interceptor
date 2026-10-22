using Xunit;

namespace HomeBlaze.Components.Tests;

public class TextBlockNamesTests
{
    [Theory]
    [InlineData("<h2 id=\"motor-status\">Motor Status</h2>\n", "Heading: Motor Status")]
    [InlineData("<p>Short text.</p>\n", "Paragraph: Short text.")]
    [InlineData("<p>Welcome to your HomeBlaze installation and its pages.</p>", "Paragraph: Welcome to your HomeBlaze...")]
    [InlineData("<ul>\n<li>First item</li>\n<li>Second</li>\n</ul>\n", "List: First item Second")]
    [InlineData("<ol start=\"3\">\n<li>Third</li>\n</ol>\n", "List: Third")]
    [InlineData("<table>\n<thead>\n<tr>\n<th>Name</th>\n<th>Value</th>\n</tr>\n</thead>\n</table>\n", "Table: Name Value")]
    [InlineData("<blockquote>\n<p>Said <em>once</em>.</p>\n</blockquote>\n", "Quote: Said once.")]
    [InlineData("<pre><code class=\"language-csharp\">var x = 1 &lt; 2;\n</code></pre>\n", "Code: var x = 1 < 2;")]
    [InlineData("<hr />\n", "Line")]
    [InlineData("<p>Averyveryverylongwordwithoutanyspaceinsideitatall</p>", "Paragraph: Averyveryverylongwordwitho...")]
    [InlineData("Plain text", "Text: Plain text")]
    public void WhenTheNameOfATextBlockIsAskedFor_ThenItIsItsKindAndItsFirstWords(string html, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, TextBlockNames.Describe(html));
    }
}
