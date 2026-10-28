using System.Text.RegularExpressions;
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// Pins the HTML that View mode renders for a page of text, which the text editing of Inline mode must not change.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public partial class ViewHtmlGoldenTests
{
    private const int Timeout = 30000;
    private const string PageName = "TextBlocks.md";
    private const string GoldenFileName = "TextBlocks.view.html";

    private readonly PlaywrightFixture _fixture;

    public ViewHtmlGoldenTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WhenAPageOfTextIsViewed_ThenItsHtmlIsTheGoldenHtml()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync($"{_fixture.ServerAddress}pages/Demo/{PageName}");
        await page.WaitUntilInteractiveAsync(Timeout);
        var content = page.Locator(".markdown-content");
        await Assertions.Expect(content.GetByText("Closing paragraph.")).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(content.GetByText("The target speed is 700 RPM", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = Timeout });
        await Assertions.Expect(content.GetByText("with the target speed 700 RPM.", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = Timeout });

        // Act
        var html = CssScopeAttribute().Replace(BlazorMarker().Replace(await content.InnerHTMLAsync(), string.Empty), string.Empty);

        // Assert
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", GoldenFileName);
        if (!File.Exists(goldenPath))
        {
            var receivedPath = Path.Combine(AppContext.BaseDirectory, "Golden", "TextBlocks.view.received.html");
            Directory.CreateDirectory(Path.GetDirectoryName(receivedPath)!);
            await File.WriteAllTextAsync(receivedPath, html);
            Assert.Fail($"There is no golden file yet. Review {receivedPath} and copy it to src/HomeBlaze/HomeBlaze.E2E.Tests/Golden/{GoldenFileName}.");
        }

        var golden = await File.ReadAllTextAsync(goldenPath);
        if (html != golden)
        {
            Assert.Fail(DescribeFirstDifference(golden, html));
        }
    }

    private static string DescribeFirstDifference(string golden, string html)
    {
        const int ContextLines = 3;

        // Splitting after every '>' keeps every character, so the lines join back to exactly the compared HTML.
        var goldenLines = TagEnd().Split(golden);
        var htmlLines = TagEnd().Split(html);

        var index = 0;
        while (index < goldenLines.Length && index < htmlLines.Length && goldenLines[index] == htmlLines[index])
        {
            index++;
        }

        var start = Math.Max(0, index - ContextLines);
        return $"""
            The HTML differs from the golden file at line {index + 1} (lines end after each '>').
            Golden:
            {FormatLines(goldenLines, start, index + ContextLines)}
            Rendered:
            {FormatLines(htmlLines, start, index + ContextLines)}
            """;
    }

    private static string FormatLines(string[] lines, int start, int end)
    {
        return string.Join(Environment.NewLine, lines
            .Skip(start)
            .Take(end - start + 1)
            .Select((line, offset) => $"{start + offset + 1,5}: {line.ReplaceLineEndings("\\n")}"));
    }

    // The comments that Blazor keeps in the document as boundaries of components.
    [GeneratedRegex("<!--!-->")]
    private static partial Regex BlazorMarker();

    // The CSS isolation scope is a build-time hash of a stylesheet's path, unrelated to what the page renders.
    [GeneratedRegex(" b-[a-z0-9]{10}=\"\"")]
    private static partial Regex CssScopeAttribute();

    [GeneratedRegex("(?<=>)")]
    private static partial Regex TagEnd();
}
