using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Storage.Pages;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// Compares how a browser builds the text of a page in Inline mode, where every text block is markup of its own, with View
/// mode, where the text between two widgets is one piece of markup. Raw HTML that leaves an element open must keep the text
/// that the element takes in its text block.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class HtmlGroupingTests
{
    // Builds the markup as Blazor inserts a markup frame: parsed on its own, and its nodes appended. Returns the HTML of all
    // frames together and of the joined markup.
    private const string CompareScript =
        """
        ([joined, frames]) => {
            const build = markups => {
                const container = document.createElement('div');
                for (const markup of markups) {
                    const template = document.createElement('template');
                    template.innerHTML = markup;
                    container.append(template.content);
                }

                return container.innerHTML;
            };

            return [build(frames), build([joined])];
        }
        """;

    private readonly PlaywrightFixture _fixture;

    public HtmlGroupingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("Before.\n\n<div class=\"note\">\n\nInside.\n\n</div>\n\nAfter.\n")]
    [InlineData("<details>\n<summary>More</summary>\n\nInside.\n\n</details>\n\nAfter.\n")]
    [InlineData("<pre>\n<div>\n</pre>\n\nAfter.\n")]
    [InlineData("<!-- c -->\n\n<script>let a;</script>\n\n<div>x</div>\n\nAfter.\n")]
    [InlineData("<div/>\n\nInside.\n")]
    [InlineData("<a name=\"x\"/>\n\nInside.\n")]
    [InlineData("<custom-x/>\n\nInside.\n")]
    [InlineData("<div>x</div> <!-- open\n\nInside.\n")]
    [InlineData("<div title=\"open>\n\nInside.\n")]
    [InlineData("<div></div><!DOCTYPE\n\nInside.\n")]
    [InlineData("Text with <b>bold\n\nInside.\n")]
    [InlineData("- An item with <b>bold\n\nInside.\n")]
    [InlineData("> <div>\n\nInside.\n")]
    [InlineData("Text with `<div>` in code and <b>bold</b>.\n\nAfter.\n")]
    public async Task WhenEachTextBlockIsRenderedOnItsOwn_ThenTheBrowserBuildsWhatTheJoinedMarkupBuilds(string source)
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        var frames = GetTextBlockMarkups(MarkdownPageParser.Parse(source));

        // Act
        var result = await page.EvaluateAsync<string[]>(CompareScript, new object[] { string.Concat(frames), frames });

        // Assert
        Assert.Equal(result[1], result[0]);
    }

    [Fact]
    public async Task WhenAnElementThatSpansBlocksIsRenderedInPieces_ThenTheBrowserBuildsSomethingElse()
    {
        // Arrange: the comparison above would see a text block per Markdig block.
        var page = await _fixture.CreatePageAsync();
        string[] frames = ["<div class=\"note\">\n", "<p>Inside.</p>\n", "</div>\n"];

        // Act
        var result = await page.EvaluateAsync<string[]>(CompareScript, new object[] { string.Concat(frames), frames });

        // Assert
        Assert.NotEqual(result[1], result[0]);
    }

    // The markup of every text block, as Inline mode renders it.
    private static string[] GetTextBlockMarkups(MarkdownPage page)
    {
        var markups = new List<string>();
        foreach (var segment in MarkdownSegmentBuilder.Build(page, linkRewriter: null, markTextBlocks: true))
        {
            var html = Assert.IsType<HtmlMarkdownSegment>(segment);
            if (html.TextBlock is not null || markups.Count == 0)
            {
                markups.Add(html.Html);
            }
            else
            {
                markups[^1] += html.Html;
            }
        }

        return markups.ToArray();
    }
}
