using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownWidgetBlockTests
{
    private const string LeafType = "HomeBlaze.Storage.Tests.Pages.TestLeaf";

    [Fact]
    public async Task WhenPageHasWidgetBlock_ThenSubjectIsCreatedWithTypedArguments()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            text

            :::{{LeafType}}#leaf name=123 count=5 enabled=true interval=00:00:01.500
            :::
            """);

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Equal("123", leaf.Name);
        Assert.Equal(5, leaf.Count);
        Assert.True(leaf.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), leaf.Interval);
        Assert.Same(leaf, page.GetBlockSubject(0));
    }

    [Fact]
    public async Task WhenBlockHasNoId_ThenKeyIsTypeNameAndPosition()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{LeafType}} name=a
            :::
            """);

        // Assert
        Assert.IsType<TestLeaf>(page.Children["TestLeaf_0"]);
    }

    [Fact]
    public async Task WhenBlockHasJsonFence_ThenArgumentsWinOverTheFence()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{LeafType}}#leaf count=7
            ```json
            { "count": 1, "tags": ["a", "b"] }
            ```
            :::
            """);

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Equal(7, leaf.Count);
        Assert.Equal(["a", "b"], leaf.Tags);
    }

    [Fact]
    public async Task WhenPageIsParsedAgain_ThenTheSameInstanceIsUpdatedInPlace()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{LeafType}}#leaf count=1
            :::
            """);
        var first = page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, $$"""
            new text above

            :::{{LeafType}}#leaf count=2
            :::
            """);

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Same(first, leaf);
        Assert.Equal(2, leaf.Count);
        Assert.True(leaf.ApplyCount >= 1);
    }

    [Theory]
    [InlineData(":::Missing.Type\n:::", "Unknown type 'Missing.Type'")]
    [InlineData(":::" + LeafType + " cont=1\n:::", "has no configuration property 'cont'")]
    [InlineData(":::" + LeafType + " count=abc\n:::", "'abc' is not a valid value for 'count'")]
    [InlineData(":::" + LeafType + "\n```json\n[1]\n```\n:::", "must contain a JSON object")]
    [InlineData(":::" + LeafType + "\n```json\n{ \"count\": 1, \"count\": 2 }\n```\n:::", "must contain a JSON object")]
    [InlineData(":::" + LeafType + "\ncontent\n:::", "cannot have content")]
    [InlineData(":::" + LeafType + "#a\n:::\n\n:::" + LeafType + "#a\n:::", "used twice")]
    public async Task WhenBlockIsWrong_ThenAnErrorBoxReplacesIt(string content, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", content);

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("markdown-block-error", html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(expectedMessage), html);
    }

    [Fact]
    public async Task WhenPageHasOldFence_ThenNoSubjectIsCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ```subject(leaf)
            { "$type": "{{LeafType}}" }
            ```
            """);

        // Assert
        Assert.All(page.Children.Values, child => Assert.IsType<HtmlSegment>(child));
        Assert.Contains("no longer supported", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenConfigurationIsWritten_ThenOnlyTheHeadLineOfThatBlockChanges()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"intro\n\n:::{LeafType}#first count=1\n:::\n\n:::{LeafType}#second count=2\n:::\n\noutro {{{{ /x }}}}\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var second = (TestLeaf)page.Children["second"];
        second.Count = 9;
        second.Name = "New Name";

        // Act
        var handled = await page.WriteConfigurationAsync(second, CancellationToken.None);

        // Assert
        Assert.True(handled);
        var written = await fixture.ReadAsync("page.md");
        Assert.Equal(
            content.Replace(
                $":::{LeafType}#second count=2",
                $":::{LeafType}#second name=\"New Name\" count=9 enabled=false interval=00:00:00"),
            written);
    }

    [Fact]
    public async Task WhenSubjectIsNotFromThePage_ThenWriteConfigurationReturnsFalse()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", "text");

        // Act
        var handled = await page.WriteConfigurationAsync(new TestLeaf(), CancellationToken.None);

        // Assert
        Assert.False(handled);
    }
}
