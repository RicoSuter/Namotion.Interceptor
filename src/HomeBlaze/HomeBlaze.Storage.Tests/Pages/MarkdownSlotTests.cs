using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor.Registry;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownSlotTests
{
    private const string Namespace = "HomeBlaze.Storage.Tests.Pages";

    private static string Html(MarkdownFragment fragment) => MarkdownPageFixture.Html(fragment.Children);

    [Fact]
    public async Task WhenWidgetHasContent_ThenDefaultSlotHoldsItAsFragment()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :::{{{Namespace}}}.TestPanel#panel title=A
            Body **bold** {{ /x/y }}
            :::
            """);

        // Assert
        var panel = Assert.IsType<TestPanel>(page.Children["panel"]);
        var content = Assert.IsType<MarkdownFragment>(panel.Content);
        Assert.Contains("<strong>bold</strong>", Html(content));
        var expression = Assert.Single(content.Children.Values.OfType<RenderExpression>());
        Assert.Equal("/x/y", expression.Path);
        Assert.Same(page, expression.Parent);
        Assert.Null(panel.Footer);
    }

    [Fact]
    public async Task WhenWidgetHasNoContent_ThenDefaultSlotIsNull()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::{{Namespace}}.TestPanel#panel
            :::
            """);

        // Assert
        Assert.Null(((TestPanel)page.Children["panel"]).Content);
    }

    [Fact]
    public async Task WhenNamedSlotBlockIsGiven_ThenItFillsThatPropertyAndTheRestIsDefault()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            one

            :::Footer
            foot
            :::

            two
            ::::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        var content = Html(Assert.IsType<MarkdownFragment>(panel.Content));
        Assert.Contains("one", content);
        Assert.Contains("two", content);
        Assert.DoesNotContain("foot", content);
        Assert.Contains("foot", Html(Assert.IsType<MarkdownFragment>(panel.Footer)));
    }

    [Fact]
    public async Task WhenDefaultSlotIsFilledByABlock_ThenItHoldsTheBlockContent()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::Content
            explicit
            :::
            ::::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Contains("explicit", Html(Assert.IsType<MarkdownFragment>(panel.Content)));
    }

    [Fact]
    public async Task WhenWidgetIsNestedInContent_ThenItIsAChildOfTheFragment()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            text

            :::{{Namespace}}.TestLeaf#leaf count=3
            :::
            ::::
            """);

        // Assert
        var content = (MarkdownFragment)((TestPanel)page.Children["panel"]).Content!;
        var leaf = Assert.IsType<TestLeaf>(content.Children["leaf"]);
        Assert.Equal(3, leaf.Count);
        Assert.Same(leaf, page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenListSlotOfConcreteTypeIsFilled_ThenEachBlockCreatesOneConfiguredValue()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::Row label="First row"
            row one
            :::
            :::Row label=Second
            :::
            ::::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Equal(["First row", "Second"], panel.Rows.Select(row => row.Label));
        Assert.Contains("row one", Html(Assert.IsType<MarkdownFragment>(panel.Rows[0].Content)));
        Assert.Null(panel.Rows[1].Content);
        Assert.Same(panel.Rows[0], page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenListSlotOfOpenTypeIsFilled_ThenEachBlockIsOneFragment()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::::{{Namespace}}.TestPanel#panel
            ::::Item
            :::{{Namespace}}.TestLeaf#a
            :::
            :::{{Namespace}}.TestLeaf#b
            :::
            ::::
            ::::Item
            plain
            ::::
            :::::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Equal(2, panel.Items.Length);
        var first = Assert.IsType<MarkdownFragment>(panel.Items[0]);
        Assert.Equal(2, first.Children.Values.OfType<TestLeaf>().Count());
        Assert.Contains("plain", Html(Assert.IsType<MarkdownFragment>(panel.Items[1])));
    }

    [Fact]
    public async Task WhenSlotBlockHoldsContentOnly_ThenItHasNoBlockSubject()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::Footer
            foot
            :::
            ::::
            """);

        // Assert
        Assert.IsType<MarkdownFragment>(((TestPanel)page.Children["panel"]).Footer);
        Assert.Null(page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenListSlotChanges_ThenItsValuesAreAttachedAndDetached()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::Row label=A
            :::
            :::Row label=B
            :::
            ::::
            """);
        var panel = (TestPanel)page.Children["panel"];
        var first = panel.Rows[0];
        var second = panel.Rows[1];
        var parentOfSecond = Assert.Single(second.TryGetRegisteredSubject()!.Parents);

        // Act
        await fixture.UpdatePageAsync(page, $$"""
            ::::{{Namespace}}.TestPanel#panel
            :::Row label=A
            :::
            ::::
            """);

        // Assert
        Assert.Same(panel, parentOfSecond.Property.Subject);
        Assert.Equal(nameof(TestPanel.Rows), parentOfSecond.Property.Name);
        Assert.Equal(1, parentOfSecond.Index);

        Assert.Same(first, Assert.Single(panel.Rows));
        var parentOfFirst = Assert.Single(first.TryGetRegisteredSubject()!.Parents);
        Assert.Same(panel, parentOfFirst.Property.Subject);
        Assert.Equal(0, parentOfFirst.Index);
        Assert.Null(second.TryGetRegisteredSubject());
    }

    [Fact]
    public async Task WhenPageIsParsedAgain_ThenSlotValuesAreReusedRecursively()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var source = $$"""
            :::::{{Namespace}}.TestPanel#panel
            ::::Row label=A
            :::{{Namespace}}.TestLeaf#leaf count=1
            :::
            ::::
            :::::
            """;
        var page = await fixture.AddPageAsync("page.md", source);
        var panel = (TestPanel)page.Children["panel"];
        var row = panel.Rows[0];
        var fragment = row.Content;
        var leaf = ((MarkdownFragment)fragment!).Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("label=A", "label=B").Replace("count=1", "count=2"));

        // Assert
        Assert.Same(panel, page.Children["panel"]);
        Assert.Same(row, panel.Rows[0]);
        Assert.Equal("B", panel.Rows[0].Label);
        Assert.Same(fragment, panel.Rows[0].Content);
        Assert.Same(leaf, ((MarkdownFragment)panel.Rows[0].Content!).Children["leaf"]);
        Assert.Equal(2, ((TestLeaf)leaf).Count);
    }

    [Fact]
    public async Task WhenArgumentOfSlotValueIsRemoved_ThenANewValueWithTheDefaultIsCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $"::::{Namespace}.TestPanel#panel\n:::Row label=A\n:::\n::::");
        var panel = (TestPanel)page.Children["panel"];
        var row = panel.Rows[0];

        // Act
        await fixture.UpdatePageAsync(page, $"::::{Namespace}.TestPanel#panel\n:::Row\n:::\n::::");

        // Assert
        Assert.Same(panel, page.Children["panel"]);
        Assert.NotSame(row, panel.Rows[0]);
        Assert.Null(panel.Rows[0].Label);
        Assert.Same(panel.Rows[0], page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenSlotBlockIsRemoved_ThenThePropertyIsCleared()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$"""
            ::::{{Namespace}}.TestPanel#panel
            text

            :::Footer
            foot
            :::
            :::Row label=A
            :::
            ::::
            """);

        // Act
        await fixture.UpdatePageAsync(page, $$"""
            :::{{Namespace}}.TestPanel#panel
            :::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Null(panel.Content);
        Assert.Null(panel.Footer);
        Assert.Empty(panel.Rows);
    }

    [Theory]
    [InlineData("::::{0}.TestPanel\n:::Footr\nx\n:::\n::::", "TestPanel has no slot 'Footr'. Available: Content, Footer, Item, Row")]
    [InlineData("::::{0}.TestPanel\n:::Footer\na\n:::\n:::Footer\nb\n:::\n::::", "The slot 'Footer' takes one block, but there are 2")]
    [InlineData("::::{0}.TestPanel\n:::Footer title=x\na\n:::\n::::", "The slot 'Footer' holds content only")]
    [InlineData("::::{0}.TestPanel\n:::Footer#name\na\n:::\n::::", "The slot 'Footer' holds content only")]
    [InlineData("::::{0}.TestPanel\n:::Footer\n```json\n{{ }}\n```\n:::\n::::", "The slot 'Footer' holds content only")]
    [InlineData("::::{0}.TestPanel\nloose\n\n:::Content\nexplicit\n:::\n::::", "The slot 'Content' is filled by a block and by loose content")]
    [InlineData("::::{0}.TestPanel\n:::Row labl=x\n:::\n::::", "TestRow has no configuration property 'labl'")]
    [InlineData(":::::{0}.TestPanel\n::::Row\n:::Footer\nx\n:::\n::::\n:::::", "TestRow has no slot 'Footer'. Available: Content")]
    [InlineData(":::::{0}.TestPanel\n::::Item\n:::Footer\nx\n:::\n::::\n:::::", "'Footer' is not a slot at this position")]
    [InlineData(":::{0}.TestLeaf\ncontent\n:::", "TestLeaf has no default slot, so it cannot have content")]
    [InlineData("::::{0}.TestLeaf\n:::Footer\nx\n:::\n::::", "TestLeaf has no slots, so 'Footer' cannot be used inside it")]
    [InlineData(":::{0}.TestInvalidSlots\n:::", "TestInvalidSlots: the slot 'Text' must hold subjects")]
    [InlineData("::::{0}.TestFaultyHost\n:::Plain\n:::\n::::", "The type 'HomeBlaze.Storage.Tests.Pages.TestPlain' of the slot 'Plain' could not be created. It must be a registered configurable subject type")]
    public async Task WhenSlotUsageIsWrong_ThenAnErrorBoxIsShown(string template, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", string.Format(template, Namespace));

        // Assert
        var html = Flatten(page.Children.Values);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(expectedMessage), html);
    }

    [Fact]
    public async Task WhenSlotBlockIsWrong_ThenTheErrorBoxNamesTheLineOfTheSlotBlock()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md",
            $"intro\n\n::::{Namespace}.TestPanel\n:::Row label=A\n:::\n:::Row labl=B\n:::\n::::");

        // Assert
        Assert.Contains("Line 6: TestRow has no configuration property", MarkdownPageFixture.Html(page.Children));
    }

    [Fact]
    public async Task WhenSlotValueThrowsInItsConstructor_ThenAnErrorBoxReplacesTheWidgetAndLaterBlocksStillWork()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md",
            $"::::{Namespace}.TestFaultyHost#host\n:::Broken\n:::\n::::\n\n:::{Namespace}.TestLeaf#leaf count=1\n:::");

        // Assert
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode("of the slot 'Broken' could not be created or updated: constructor failed"),
            MarkdownPageFixture.Html(page.Children));
        Assert.False(page.Children.ContainsKey("host"));
        Assert.Equal(1, Assert.IsType<TestLeaf>(page.Children["leaf"]).Count);
    }

    [Theory]
    [InlineData(TestFaulty.ThrowMode, "apply failed")]
    [InlineData(TestFaulty.CancelMode, "apply cancelled")]
    public async Task WhenSlotValueFailsWhileItIsUpdated_ThenAnErrorBoxReplacesTheWidgetAndLaterBlocksStillWork(
        string mode, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var source = $"::::{Namespace}.TestFaultyHost#host\n:::Faulty mode=ok\n:::\n::::\n\n:::{Namespace}.TestLeaf#leaf count=1\n:::";
        var page = await fixture.AddPageAsync("page.md", source);
        var leaf = page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("mode=ok", $"mode={mode}").Replace("count=1", "count=2"));

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("of the slot 'Faulty' could not be created or updated"), html);
        Assert.Contains(expectedMessage, html);
        Assert.False(page.Children.ContainsKey("host"));
        Assert.Same(leaf, page.Children["leaf"]);
        Assert.Equal(2, ((TestLeaf)leaf).Count);
    }

    [Fact]
    public async Task WhenWidgetWithSlotsFails_ThenBlocksNestedInItHaveNoBlockSubject()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :::::{{Namespace}}.TestPanel#panel
            ::::Item
            :::{{Namespace}}.TestLeaf#leaf
            :::
            ::::
            ::::Row label=A
            ::::
            ::::Footer title=x
            ::::
            :::::

            :::{{Namespace}}.TestLeaf#after
            :::
            """);

        // Assert
        Assert.Contains("holds content only", MarkdownPageFixture.Html(page.Children));
        for (var blockIndex = 0; blockIndex <= 4; blockIndex++)
        {
            Assert.Null(page.GetBlockSubject(blockIndex));
        }

        Assert.Same(page.Children["after"], page.GetBlockSubject(5));
    }

    [Theory]
    [InlineData("::::{0}.TestPanel\n:::Row#a label=A\n:::\n::::\n\n:::{0}.TestLeaf#a\n:::", 1)]
    [InlineData("::::{0}.TestPanel\n:::{0}.TestLeaf#a\n:::\n::::\n\n:::{0}.TestLeaf#a\n:::", 1)]
    [InlineData("::::{0}.TestPanel\n:::Row#a labl=A\n:::\n::::\n\n:::{0}.TestLeaf#a\n:::", 0)]
    [InlineData(":::{0}.TestLeaf#a\n:::\n\n::::{0}.TestPanel\n:::Row#a label=A\n:::\n::::", 0)]
    [InlineData(":::::{0}.TestPanel\n::::Item\n:::{0}.TestLeaf#a\n:::\n::::\n::::Item\n:::{0}.TestLeaf#a\n:::\n::::\n:::::", 1)]
    public async Task WhenNameIsUsedAgainAtAnotherDepth_ThenTheSecondBlockIsAnErrorBox(string template, int expectedPanels)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", string.Format(template, Namespace));

        // Assert
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode("The name 'a' is used twice on this page."),
            Flatten(page.Children.Values));
        Assert.Equal(expectedPanels, page.Children.Values.OfType<TestPanel>().Count());
    }

    [Fact]
    public async Task WhenConcreteSlotValueIsWrittenBack_ThenOnlyItsHeadLineChanges()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $"::::{Namespace}.TestPanel#panel title=T\n:::Row label=A\nrow text\n:::\n::::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var row = ((TestPanel)page.Children["panel"]).Rows[0];
        row.Label = "New Label";

        // Act
        var handled = await page.WriteConfigurationAsync(row, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(content.Replace(":::Row label=A", ":::Row label=\"New Label\""), await fixture.ReadAsync("page.md"));
    }

    private static string Flatten(IEnumerable<Namotion.Interceptor.IInterceptorSubject?> subjects) =>
        string.Concat(subjects.Select(subject => subject switch
        {
            HtmlSegment html => html.Html,
            MarkdownFragment fragment => Flatten(fragment.Children.Values),
            TestPanel panel => Flatten([panel.Content, panel.Footer, .. panel.Items, .. panel.Rows]),
            TestRow row => Flatten([row.Content]),
            _ => string.Empty
        }));
}
