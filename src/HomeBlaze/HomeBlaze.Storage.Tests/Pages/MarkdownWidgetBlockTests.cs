using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownWidgetBlockTests
{
    private const string LeafType = "HomeBlaze.Storage.Tests.Pages.TestLeaf";
    private const string FaultyType = "HomeBlaze.Storage.Tests.Pages.TestFaulty";

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
    [InlineData(":::" + LeafType + " count=1.5\n:::", "'1.5' is not a valid value for 'count'. Expected a value of type Int32")]
    [InlineData(":::" + LeafType + " interval=abc\n:::", "'abc' is not a valid value for 'interval'. Expected a value of type TimeSpan")]
    [InlineData(":::" + LeafType + "\n```json\n[1]\n```\n:::", "must contain a JSON object")]
    [InlineData(":::" + LeafType + "\n```json\n{ \"count\": 1, \"count\": 2 }\n```\n:::", "without duplicate keys")]
    [InlineData(":::" + LeafType + "\n```json\n{ \"cont\": 1 }\n```\n:::", "has no configuration property 'cont'")]
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

    [Theory]
    [InlineData("HomeBlaze.Storage.Tests.Pages.TestAbstractConfigurable")]
    [InlineData("HomeBlaze.Storage.Tests.Pages.ITestConfigurable")]
    public async Task WhenTypeCannotBeInstantiated_ThenAnErrorBoxReplacesItAndLaterBlocksStillWork(string typeName)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $":::{typeName}\n:::\n\n:::{LeafType}#leaf count=1\n:::");

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("markdown-block-error", html);
        Assert.Contains(typeName, html);
        Assert.Contains("cannot be created", html);
        Assert.Equal(1, Assert.IsType<TestLeaf>(page.Children["leaf"]).Count);
    }

    [Fact]
    public async Task WhenSubjectThrowsInItsConstructor_ThenAnErrorBoxReplacesItAndLaterBlocksStillWork()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md",
            $":::HomeBlaze.Storage.Tests.Pages.TestThrowingConstructor\n:::\n\n:::{LeafType}#leaf count=1\n:::");

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("could not be created", html);
        Assert.Contains("constructor failed", html);
        Assert.Equal(1, Assert.IsType<TestLeaf>(page.Children["leaf"]).Count);
    }

    [Fact]
    public async Task WhenSubjectThrowsWhileItsConfigurationIsApplied_ThenAnErrorBoxReplacesItAndLaterBlocksStillWork()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md",
            $":::{FaultyType}#faulty mode=ok\n:::\n\n:::{LeafType}#leaf count=1\n:::");
        var leaf = page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(
            page,
            $":::{FaultyType}#faulty mode={TestFaulty.ThrowMode}\n:::\n\n:::{LeafType}#leaf count=2\n:::");

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("could not be created", html);
        Assert.Contains("apply failed", html);
        Assert.False(page.Children.ContainsKey("faulty"));
        Assert.Same(leaf, page.Children["leaf"]);
        Assert.Equal(2, ((TestLeaf)leaf).Count);
    }

    [Fact]
    public async Task WhenReloadFails_ThenContentAndChildrenStayAsTheyWere()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"---\ntitle: Before\n---\n\n:::{FaultyType}#faulty mode=ok\n:::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var children = page.Children;
        var faulty = page.GetBlockSubject(0);

        // Act
        var exception = await Record.ExceptionAsync(() => fixture.UpdatePageAsync(
            page,
            $"---\ntitle: After\n---\n\nnew text\n\n:::{FaultyType}#faulty mode={TestFaulty.CancelMode}\n:::\n"));

        // Assert
        Assert.IsType<OperationCanceledException>(exception);
        Assert.Equal(content, page.Content);
        Assert.Equal("Before", page.Title);
        Assert.Same(children, page.Children);
        Assert.Same(faulty, page.GetBlockSubject(0));
    }

    [Fact]
    public async Task WhenArgumentIsRemoved_ThenANewInstanceWithTheDefaultValueIsCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":::{LeafType}#leaf count=1 name=x\n:::");
        var first = page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, $":::{LeafType}#leaf count=1\n:::");

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.NotSame(first, leaf);
        Assert.Null(leaf.Name);
        Assert.Equal(1, leaf.Count);
        Assert.Same(leaf, page.GetBlockSubject(0));
    }

    [Fact]
    public async Task WhenArgumentIsAddedOrChanged_ThenTheInstanceIsKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":::{LeafType}#leaf count=1\n:::");
        var first = page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, $":::{LeafType}#leaf count=2 name=x\n:::");

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Same(first, leaf);
        Assert.Equal(2, leaf.Count);
        Assert.Equal("x", leaf.Name);
    }

    [Theory]
    [InlineData(":::" + LeafType + "#TestLeaf_2\n:::\n\n:::" + LeafType + "\n:::")]
    [InlineData(":::" + LeafType + "\n:::\n\n:::" + LeafType + "#TestLeaf_0\n:::")]
    public async Task WhenIdEqualsAPositionalKey_ThenTheSecondBlockIsAnErrorBox(string content)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", content);

        // Assert
        Assert.Contains("used twice", MarkdownPageFixture.Html(page.Children));
        var leaf = Assert.Single(page.Children.Values.OfType<TestLeaf>());
        Assert.Same(leaf, page.GetBlockSubject(0));
        Assert.Null(page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenIdOfAnErrorBlockIsUsedAgain_ThenTheSecondBlockIsAnErrorBox()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md",
            $":::{LeafType}#a cont=1\n:::\n\n:::{LeafType}#a\n:::");

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("has no configuration property", html);
        Assert.Contains("used twice", html);
        Assert.Empty(page.Children.Values.OfType<TestLeaf>());
    }

    [Fact]
    public async Task WhenTwoPagesAreEmpty_ThenTheyDoNotShareTheirChildren()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var first = await fixture.AddPageAsync("first.md", string.Empty);
        var second = await fixture.AddPageAsync("second.md", string.Empty);

        // Assert
        Assert.Empty(first.Children);
        Assert.NotSame(first.Children, second.Children);
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
    public async Task WhenConfigurationIsWrittenTwice_ThenBothBlocksAreUpdated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"intro\n\n:::{LeafType}#first count=1\n:::\n\n:::{LeafType}#second count=2\n:::\n\noutro\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var first = (TestLeaf)page.Children["first"];
        var second = (TestLeaf)page.Children["second"];

        // Act
        first.Count = 5;
        var firstHandled = await page.WriteConfigurationAsync(first, CancellationToken.None);
        second.Count = 7;
        var secondHandled = await page.WriteConfigurationAsync(second, CancellationToken.None);

        // Assert
        Assert.True(firstHandled);
        Assert.True(secondHandled);
        var expected =
            $"intro\n\n:::{LeafType}#first count=5 enabled=false interval=00:00:00\n:::\n\n" +
            $":::{LeafType}#second count=7 enabled=false interval=00:00:00\n:::\n\noutro\n";
        Assert.Equal(expected, await fixture.ReadAsync("page.md"));
        Assert.Equal(expected, page.Content);
    }

    [Fact]
    public async Task WhenWriteBackRemovesAnArgument_ThenTheReloadKeepsTheInstance()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":::{LeafType}#leaf name=x count=1\n:::\n");
        var leaf = (TestLeaf)page.Children["leaf"];
        leaf.Name = null;
        await page.WriteConfigurationAsync(leaf, CancellationToken.None);

        // Act
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.DoesNotContain("name=", page.Content);
        Assert.Same(leaf, page.Children["leaf"]);
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
