using System.Net;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownMarkupTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static string?[] GetMarkup(params IInterceptorSubject[] children) =>
        MarkdownMarkup.GetRenderSegments(children, timeZone: null).Select(segment => segment.Html).ToArray();

    private static async Task<MarkdownFile> AddPageAsync(string text)
    {
        var fixture = await MarkdownPageFixture.CreateAsync();
        return await fixture.AddPageAsync("page.md", $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5
            ::

            {{{text}}}
            """);
    }

    private static string GetPowerText(MarkdownFile page) =>
        WebUtility.HtmlEncode(page.Children.Values.OfType<RenderExpression>().Single().GetText(null));

    [Fact]
    public void WhenABlockStartsAtTheStartOfASegment_ThenTheHtmlBeforeIsMarkupOfItsOwn()
    {
        // Act
        var markup = GetMarkup(new HtmlSegment("x"), new HtmlSegment("<p>A</p>", [0]));

        // Assert
        Assert.Equal(new[] { "x", "<p>A</p>" }, markup);
    }

    [Fact]
    public void WhenABlockStartsAtTheEndOfASegment_ThenTheHtmlAfterIsMarkupOfItsOwn()
    {
        // Act
        var markup = GetMarkup(new HtmlSegment("<p>A</p>", [8]), new HtmlSegment("y"));

        // Assert
        Assert.Equal(new[] { "<p>A</p>", "y" }, markup);
    }

    [Fact]
    public void WhenABlockStartsInsideASegment_ThenEachPartIsMarkupOfItsOwn()
    {
        // Act
        var markup = GetMarkup(new HtmlSegment("x"), new HtmlSegment("<p>A</p><p>B</p>", [8]), new HtmlSegment("y"));

        // Assert
        Assert.Equal(new[] { "x<p>A</p>", "<p>B</p>y" }, markup);
    }

    [Fact]
    public void WhenCompleteBlocksAreRenderedTwice_ThenTheirMarkupIsTheSameStrings()
    {
        // Arrange
        var segment = new HtmlSegment("<p>A</p><p>B</p><p>C</p>", [0, 8, 16, 24]);

        // Act
        var first = GetMarkup(segment);
        var second = GetMarkup(segment);

        // Assert
        Assert.Equal(new[] { "<p>A</p>", "<p>B</p>", "<p>C</p>" }, first);
        Assert.All(first.Zip(second), pair => Assert.Same(pair.First, pair.Second));
    }

    [Fact]
    public async Task WhenAnExpressionIsInsideAParagraph_ThenItIsJoinedWithTheHtmlOfItsBlockOnly()
    {
        // Arrange
        var page = await AddPageAsync("First.\n\nSecond {{ meter/Power }} end.\n\nThird.");

        // Act
        var first = MarkdownMarkup.GetRenderSegments(page.Children.Values, timeZone: null);
        var second = MarkdownMarkup.GetRenderSegments(page.Children.Values, timeZone: null);

        // Assert
        Assert.Equal(
            new[] { null, "\n<p>First.</p>\n", $"<p>Second {GetPowerText(page)} end.</p>\n", "<p>Third.</p>\n" },
            first.Select(segment => segment.Html));
        Assert.IsType<TestMeter>(first[0].Widget);
        Assert.Same(first[1].Html, second[1].Html);
        Assert.Same(first[3].Html, second[3].Html);
    }

    [Fact]
    public async Task WhenAnExpressionIsInARawHtmlAttribute_ThenItsBlockIsOneMarkupString()
    {
        // Arrange
        var page = await AddPageAsync("An <img alt=\"{{ meter/Power }}\"> image.\n\nAfter.");

        // Act
        var markup = MarkdownMarkup.GetRenderSegments(page.Children.Values, timeZone: null);

        // Assert
        Assert.Equal(
            new[] { null, $"\n<p>An <img alt=\"{GetPowerText(page)}\"> image.</p>\n", "<p>After.</p>\n" },
            markup.Select(segment => segment.Html));
    }
}
