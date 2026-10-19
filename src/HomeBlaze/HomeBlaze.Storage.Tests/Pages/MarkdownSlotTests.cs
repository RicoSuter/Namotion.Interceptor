using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
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
            :: {{{Namespace}}}.TestPanel#panel Title=A
            Body **bold** {{ /x/y }}
            ::
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
            :: {{Namespace}}.TestPanel#panel
            ::
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
            :: {{Namespace}}.TestPanel#panel
            one

            ::: Footer
            foot
            :::

            two
            ::
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
            :: {{Namespace}}.TestPanel#panel
            ::: Content
            explicit
            :::
            ::
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
            :: {{Namespace}}.TestPanel#panel
            text

            ::: {{Namespace}}.TestLeaf#leaf Count=3
            :::
            ::
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
            :: {{Namespace}}.TestPanel#panel
            ::: Row Label="First row"
            row one
            :::
            ::: Row Label=Second
            :::
            ::
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
            :: {{Namespace}}.TestPanel#panel
            ::: Item
            :::: {{Namespace}}.TestLeaf#a
            ::::
            :::: {{Namespace}}.TestLeaf#b
            ::::
            :::
            ::: Item
            plain
            :::
            ::
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
            :: {{Namespace}}.TestPanel#panel
            ::: Footer
            foot
            :::
            ::
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
            :: {{Namespace}}.TestPanel#panel
            ::: Row Label=A
            :::
            ::: Row Label=B
            :::
            ::
            """);
        var panel = (TestPanel)page.Children["panel"];
        var first = panel.Rows[0];
        var second = panel.Rows[1];
        var parentOfSecond = Assert.Single(second.TryGetRegisteredSubject()!.Parents);

        // Act
        await fixture.UpdatePageAsync(page, $$"""
            :: {{Namespace}}.TestPanel#panel
            ::: Row Label=A
            :::
            ::
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
            :: {{Namespace}}.TestPanel#panel
            ::: Row Label=A
            :::: {{Namespace}}.TestLeaf#leaf Count=1
            ::::
            :::
            ::
            """;
        var page = await fixture.AddPageAsync("page.md", source);
        var panel = (TestPanel)page.Children["panel"];
        var row = panel.Rows[0];
        var fragment = row.Content;
        var leaf = ((MarkdownFragment)fragment!).Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("Label=A", "Label=B").Replace("Count=1", "Count=2"));

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
        var page = await fixture.AddPageAsync("page.md", $":: {Namespace}.TestPanel#panel\n::: Row Label=A\n:::\n::");
        var panel = (TestPanel)page.Children["panel"];
        var row = panel.Rows[0];

        // Act
        await fixture.UpdatePageAsync(page, $":: {Namespace}.TestPanel#panel\n::: Row\n:::\n::");

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
            :: {{Namespace}}.TestPanel#panel
            text

            ::: Footer
            foot
            :::
            ::: Row Label=A
            :::
            ::
            """);

        // Act
        await fixture.UpdatePageAsync(page, $$"""
            :: {{Namespace}}.TestPanel#panel
            ::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Null(panel.Content);
        Assert.Null(panel.Footer);
        Assert.Empty(panel.Rows);
    }

    [Theory]
    [InlineData(":: {0}.TestPanel\n::: Footr\nx\n:::\n::", "TestPanel has no slot 'Footr'. Available: Content, Footer, Item, Row")]
    [InlineData(":: {0}.TestPanel\n::: Footer\na\n:::\n::: Footer\nb\n:::\n::", "The slot 'Footer' takes one block, but there are 2")]
    [InlineData(":: {0}.TestPanel\n::: Footer Title=x\na\n:::\n::", "The slot 'Footer' holds content only")]
    [InlineData(":: {0}.TestPanel\n::: Footer#name\na\n:::\n::", "The slot 'Footer' holds content only")]
    [InlineData(":: {0}.TestPanel\n::: Footer\n```json\n{{ }}\n```\n:::\n::", "The slot 'Footer' holds content only")]
    [InlineData(":: {0}.TestPanel\nloose\n\n::: Content\nexplicit\n:::\n::", "The slot 'Content' is filled by a block and by loose content")]
    [InlineData(":: {0}.TestPanel\n::: Row Labl=x\n:::\n::", "TestRow has no configuration property 'Labl'")]
    [InlineData(":: {0}.TestPanel\n::: Row\n:::: Footer\nx\n::::\n:::\n::", "TestRow has no slot 'Footer'. Available: Content")]
    [InlineData(":: {0}.TestPanel\n::: Item\n:::: Footer\nx\n::::\n:::\n::", "'Footer' is not a slot at this position")]
    [InlineData(":: {0}.TestLeaf\ncontent\n::", "TestLeaf has no default slot, so it cannot have content")]
    [InlineData(":: {0}.TestLeaf\n::: Footer\nx\n:::\n::", "TestLeaf has no slots, so 'Footer' cannot be used inside it")]
    [InlineData(":: {0}.TestInvalidSlots\n::", "TestInvalidSlots: the slot 'Text' must hold subjects")]
    [InlineData(":: {0}.TestFaultyHost\n::: Plain\n:::\n::", "The type 'HomeBlaze.Storage.Tests.Pages.TestPlain' of the slot 'Plain' could not be created. It must be a registered configurable subject type")]
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
            $"intro\n\n:: {Namespace}.TestPanel\n::: Row Label=A\n:::\n::: Row Labl=B\n:::\n::");

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
            $":: {Namespace}.TestFaultyHost#host\n::: Broken\n:::\n::\n\n:: {Namespace}.TestLeaf#leaf Count=1\n::");

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
        var source = $":: {Namespace}.TestFaultyHost#host\n::: Faulty Mode=ok\n:::\n::\n\n:: {Namespace}.TestLeaf#leaf Count=1\n::";
        var page = await fixture.AddPageAsync("page.md", source);
        var leaf = page.Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("Mode=ok", $"Mode={mode}").Replace("Count=1", "Count=2"));

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
            :: {{Namespace}}.TestPanel#panel
            ::: Item
            :::: {{Namespace}}.TestLeaf#leaf
            ::::
            :::
            ::: Row Label=A
            :::
            ::: Footer Title=x
            :::
            ::

            :: {{Namespace}}.TestLeaf#after
            ::
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
    [InlineData(":: {0}.TestPanel\n::: Row#a Label=A\n:::\n::\n\n:: {0}.TestLeaf#a\n::", 1)]
    [InlineData(":: {0}.TestPanel\n::: {0}.TestLeaf#a\n:::\n::\n\n:: {0}.TestLeaf#a\n::", 1)]
    [InlineData(":: {0}.TestPanel\n::: Row#a Labl=A\n:::\n::\n\n:: {0}.TestLeaf#a\n::", 0)]
    [InlineData(":: {0}.TestLeaf#a\n::\n\n:: {0}.TestPanel\n::: Row#a Label=A\n:::\n::", 0)]
    [InlineData(":: {0}.TestPanel\n::: Item\n:::: {0}.TestLeaf#a\n::::\n:::\n::: Item\n:::: {0}.TestLeaf#a\n::::\n:::\n::", 1)]
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
        var content = $":: {Namespace}.TestPanel#panel Title=T\n::: Row Label=A\nrow text\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var row = ((TestPanel)page.Children["panel"]).Rows[0];
        row.Label = "New Label";

        // Act
        var handled = await page.WriteConfigurationAsync(row, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(content.Replace("::: Row Label=A", "::: Row Label=\"New Label\""), await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenSlotValueIsWrittenBackAndThePageIsLoadedAgain_ThenTheInstanceIsKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {Namespace}.TestPanel#panel\n::: Row Label=A\nrow text\n:::\n::\n");
        var panel = (TestPanel)page.Children["panel"];
        var row = panel.Rows[0];
        var content = row.Content;
        row.Label = null;
        await page.WriteConfigurationAsync(row, CancellationToken.None);

        // Act
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.DoesNotContain("Label=", page.Content);
        Assert.Same(panel, page.Children["panel"]);
        Assert.Same(row, Assert.Single(panel.Rows));
        Assert.Same(content, row.Content);
        Assert.Same(row, page.GetBlockSubject(1));
    }

    [Fact]
    public async Task WhenSubjectNestedInSlotsIsSavedTheWayTheEditPanelSavesIt_ThenItsPageWritesOnlyItsBlock()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content =
            $":: {Namespace}.TestPanel#panel Title=T\n::: Row Label=A\n:::: {Namespace}.TestLeaf#leaf Count=1\n::::\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var row = ((TestPanel)page.Children["panel"]).Rows[0];
        var leaf = (TestLeaf)((MarkdownFragment)row.Content!).Children["leaf"];
        var rootManager = fixture.Services.GetRequiredService<RootManager>();
        leaf.Count = 5;

        // Act: the edit panel writes the owner of the configuration through the nearest writer above it.
        var owner = leaf.TryGetConfigurationOwner();
        var writer = rootManager.GetConfigurationWriter(owner!);
        var handled = await writer.WriteConfigurationAsync(owner!, CancellationToken.None);

        // Assert: the page is the writer, not the storage above it, which is a configuration writer as well.
        Assert.Same(leaf, owner);
        Assert.Same(page, writer);
        Assert.True(handled);
        Assert.Equal(
            content.Replace("#leaf Count=1", "#leaf Count=5"),
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenSlotValueIsSavedTheWayTheEditPanelSavesIt_ThenItsPageWritesOnlyItsBlock()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content = $":: {Namespace}.TestPanel#panel Title=T\n::: Row Label=A\nrow text\n:::\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var row = ((TestPanel)page.Children["panel"]).Rows[0];
        var rootManager = fixture.Services.GetRequiredService<RootManager>();
        row.Label = "B";

        // Act
        var owner = row.TryGetConfigurationOwner();
        var writer = rootManager.GetConfigurationWriter(owner!);
        var handled = await writer.WriteConfigurationAsync(owner!, CancellationToken.None);

        // Assert
        Assert.Same(row, owner);
        Assert.Same(page, writer);
        Assert.True(handled);
        Assert.Equal(content.Replace("::: Row Label=A", "::: Row Label=B"), await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenNamedSlotBlockIsEmpty_ThenAnOpenSlotGetsAnEmptyFragmentAndAConcreteSlotAValueWithoutContent()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :: {{Namespace}}.TestPanel#panel
            ::: Footer
            :::
            ::: Item
            :::
            ::: Row
            :::
            ::
            """);

        // Assert
        var panel = (TestPanel)page.Children["panel"];
        Assert.Empty(Assert.IsType<MarkdownFragment>(panel.Footer).Children);
        Assert.Empty(Assert.IsType<MarkdownFragment>(Assert.Single(panel.Items)).Children);
        Assert.Null(Assert.Single(panel.Rows).Content);
        Assert.Null(panel.Content);
    }

    [Fact]
    public async Task WhenNamedSlotBlockIsInsertedBeforeOthers_ThenTheNamedValuesAndTheirContentAreKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var source = $$"""
            :: {{Namespace}}.TestPanel#panel
            ::: Row#one Label=1
            :::
            ::: Row#two Label=2
            :::
            ::: Row#three Label=3
            :::: {{Namespace}}.TestLeaf#leaf Count=1
            ::::
            :::
            ::
            """;
        var page = await fixture.AddPageAsync("page.md", source);
        var panel = (TestPanel)page.Children["panel"];
        var rows = panel.Rows;
        var fragment = rows[2].Content;
        var leaf = ((MarkdownFragment)fragment!).Children["leaf"];

        // Act
        await fixture.UpdatePageAsync(page, source.Replace("::: Row#one", "::: Row#zero Label=0\n:::\n::: Row#one"));

        // Assert
        Assert.Equal(["0", "1", "2", "3"], panel.Rows.Select(row => row.Label));
        Assert.Same(rows[0], panel.Rows[1]);
        Assert.Same(rows[1], panel.Rows[2]);
        Assert.Same(rows[2], panel.Rows[3]);
        Assert.Same(fragment, panel.Rows[3].Content);
        Assert.Same(leaf, ((MarkdownFragment)panel.Rows[3].Content!).Children["leaf"]);
        Assert.Same(panel.Rows[3], page.GetBlockSubject(4));
    }

    [Fact]
    public async Task WhenNamedSlotBlocksAreReordered_ThenTheValuesAreKeptAndFollowThePage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {Namespace}.TestPanel#panel\n::: Row#a Label=A\n:::\n::: Row#b Label=B\n:::\n::");
        var panel = (TestPanel)page.Children["panel"];
        var rows = panel.Rows;

        // Act
        await fixture.UpdatePageAsync(
            page, $":: {Namespace}.TestPanel#panel\n::: Row#b Label=B\n:::\n::: Row#a Label=A\n:::\n::");

        // Assert
        Assert.Equal(["B", "A"], panel.Rows.Select(row => row.Label));
        Assert.Same(rows[1], panel.Rows[0]);
        Assert.Same(rows[0], panel.Rows[1]);
    }

    [Fact]
    public async Task WhenNamedSlotBlockIsRemoved_ThenTheOtherNamedValuesAreKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md",
            $":: {Namespace}.TestPanel#panel\n::: Row#a Label=A\n:::\n::: Row#b Label=B\n:::\n::: Row#c Label=C\n:::\n::");
        var panel = (TestPanel)page.Children["panel"];
        var rows = panel.Rows;

        // Act
        await fixture.UpdatePageAsync(
            page, $":: {Namespace}.TestPanel#panel\n::: Row#a Label=A\n:::\n::: Row#c Label=C\n:::\n::");

        // Assert
        Assert.Equal(["A", "C"], panel.Rows.Select(row => row.Label));
        Assert.Same(rows[0], panel.Rows[0]);
        Assert.Same(rows[2], panel.Rows[1]);
    }

    [Fact]
    public async Task WhenSlotBlockWithoutNameIsInsertedBeforeOthers_ThenTheValuesAreReusedByPosition()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {Namespace}.TestPanel#panel\n::: Row Label=A\n:::\n::: Row Label=B\n:::\n::");
        var panel = (TestPanel)page.Children["panel"];
        var rows = panel.Rows;

        // Act
        await fixture.UpdatePageAsync(
            page,
            $":: {Namespace}.TestPanel#panel\n::: Row Label=New\n:::\n::: Row Label=A\n:::\n::: Row Label=B\n:::\n::");

        // Assert
        Assert.Equal(["New", "A", "B"], panel.Rows.Select(row => row.Label));
        Assert.Same(rows[0], panel.Rows[0]);
        Assert.Same(rows[1], panel.Rows[1]);
        Assert.DoesNotContain(panel.Rows[2], rows);
    }

    [Fact]
    public async Task WhenNamedAndUnnamedSlotBlocksAreMixed_ThenNamedMatchByNameAndTheRestByPositionAmongThemselves()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {Namespace}.TestPanel#panel\n::: Row#named Label=N\n:::\n::: Row Label=U\n:::\n::");
        var panel = (TestPanel)page.Children["panel"];
        var named = panel.Rows[0];
        var unnamed = panel.Rows[1];

        // Act
        await fixture.UpdatePageAsync(
            page,
            $":: {Namespace}.TestPanel#panel\n::: Row Label=X\n:::\n::: Row#named Label=N\n:::\n::: Row Label=U\n:::\n::");

        // Assert
        Assert.Equal(["X", "N", "U"], panel.Rows.Select(row => row.Label));
        Assert.Same(unnamed, panel.Rows[0]);
        Assert.Same(named, panel.Rows[1]);
        Assert.NotSame(named, panel.Rows[2]);
        Assert.NotSame(unnamed, panel.Rows[2]);
    }

    [Theory]
    [InlineData("::: Row#other Label=A")]
    [InlineData("::: Row Label=A")]
    public async Task WhenNamedSlotBlockIsRenamedOrLosesItsName_ThenANewValueIsCreated(string newHead)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {Namespace}.TestPanel#panel\n::: Row#name Label=A\n:::\n::");
        var panel = (TestPanel)page.Children["panel"];
        var row = panel.Rows[0];

        // Act
        await fixture.UpdatePageAsync(page, $":: {Namespace}.TestPanel#panel\n{newHead}\n:::\n::");

        // Assert
        Assert.Same(panel, page.Children["panel"]);
        Assert.NotSame(row, Assert.Single(panel.Rows));
    }

    [Fact]
    public async Task WhenNameIsFirstUsedInsideAWidgetThatFails_ThenALaterBlockWithThatNameIsAnErrorBox()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :: {{Namespace}}.TestPanel
            ::: Footr
            :::: {{Namespace}}.TestLeaf#a
            ::::
            :::
            ::

            :: {{Namespace}}.TestLeaf#a
            ::
            """);

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("TestPanel has no slot 'Footr'"), html);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Line 8: The name 'a' is used twice on this page."), html);
        Assert.All(page.Children.Values, child => Assert.IsType<HtmlSegment>(child));
    }

    [Fact]
    public async Task WhenNameIsUsedInLooseContentAndThenInASlotBlock_ThenTheSlotBlockIsTheError()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :: {{Namespace}}.TestPanel
            ::: {{Namespace}}.TestLeaf#a
            :::
            ::: Row#a Label=A
            :::
            ::
            """);

        // Assert
        Assert.Contains(
            System.Net.WebUtility.HtmlEncode("Line 4: The name 'a' is used twice on this page."),
            MarkdownPageFixture.Html(page.Children));
        Assert.Empty(page.Children.Values.OfType<TestPanel>());
    }

    [Fact]
    public async Task WhenTypeDeclaresItsSlotsWrongly_ThenNoInstanceIsCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $":: {Namespace}.TestInvalidSlots\n::");

        // Assert
        Assert.Contains("must hold subjects", MarkdownPageFixture.Html(page.Children));
        Assert.Equal(0, TestInvalidSlots.CreatedCount);
    }

    [Fact]
    public async Task WhenSettingASlotFails_ThenTheErrorBoxShowsTheMessageOfTheFailure()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {Namespace}.TestRequiredSlotHost#host\n::: Needed\ntext\n:::\n::");
        var host = Assert.IsType<TestRequiredSlotHost>(page.Children["host"]);

        // Act
        await fixture.UpdatePageAsync(page, $":: {Namespace}.TestRequiredSlotHost#host\n::");

        // Assert
        var html = MarkdownPageFixture.Html(page.Children);
        Assert.Contains("could not be created or updated", html);
        Assert.Contains("Needed", html);
        Assert.DoesNotContain("target of an invocation", html);
        Assert.NotNull(host.Needed);
    }

    [Fact]
    public async Task WhenTwoReloadsOverlap_ThenThePageIsTheResultOfTheLastOne()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var first = Source("one", "one");
        var second = Source("two", "two-a", "two-b");
        var third = Source("three", "three");
        var page = await fixture.AddPageAsync("page.md", first);
        var gate = (TestGate)page.Children["gate"];
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.Gate = release.Task;

        // Act
        var firstReload = fixture.UpdatePageAsync(page, second);
        await gate.Entered.Task;
        gate.Gate = null;
        await fixture.WriteAsync(page.Name, third);
        var secondReload = page.OnFileChangedAsync(CancellationToken.None);
        var secondReloadWaited = !secondReload.IsCompleted;
        release.SetResult();
        await Task.WhenAll(firstReload, secondReload);

        // Assert
        Assert.True(secondReloadWaited);
        Assert.Equal(third, page.Content);
        Assert.Equal("three", gate.Label);
        var panel = (TestPanel)page.Children["panel"];
        var row = Assert.Single(panel.Rows);
        Assert.Equal("three", row.Label);
        Assert.Same(gate, page.GetBlockSubject(0));
        Assert.Same(panel, page.GetBlockSubject(1));
        Assert.Same(row, page.GetBlockSubject(2));
        Assert.Null(page.GetBlockSubject(3));

        static string Source(string gateLabel, params string[] rowLabels) =>
            $":: {Namespace}.TestGate#gate Label={gateLabel}\n::\n\n:: {Namespace}.TestPanel#panel\n" +
            string.Concat(rowLabels.Select(label => $"::: Row Label={label}\n:::\n")) +
            "::\n";
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
