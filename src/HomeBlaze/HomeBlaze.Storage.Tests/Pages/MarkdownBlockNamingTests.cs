using System.Net;
using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace HomeBlaze.Storage.Tests.Pages;

/// <summary>
/// The configuration of a block is named by the property names of its type: on the head line, in the json
/// block at every depth, and in what is written back.
/// </summary>
[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownBlockNamingTests
{
    private const string Namespace = "HomeBlaze.Storage.Tests.Pages";
    private const string LeafType = $"{Namespace}.TestLeaf";
    private const string PanelType = $"{Namespace}.TestPanel";
    private const string GridType = "HomeBlaze.Components.GridLayout";
    private const string WidgetType = "HomeBlaze.Components.Widget";

    private const string GridPage = $$"""
        :: {{GridType}}#grid Rows=1 Columns=2
        ```json
        {
          "Cells": [
            {
              "Row": 0,
              "Column": 1,
              "RowSpan": 1,
              "ColumnSpan": 2,
              "Child": {
                "$type": "{{WidgetType}}",
                "Path": "../Compressor"
              }
            }
          ]
        }
        ```
        ::
        """;

    private static Task<MarkdownPageFixture> CreateFixtureWithComponentsAsync() =>
        MarkdownPageFixture.CreateAsync(services =>
        {
            // The type registry reads the provider on first use, so the assembly is added before anything is created.
            var typeProvider = (TypeProvider)services
                .Single(service => service.ServiceType == typeof(TypeProvider))
                .ImplementationInstance!;

            typeProvider.AddAssembly(typeof(GridLayout).Assembly);
        });

    private static string DecodedHtml(MarkdownFile page) =>
        WebUtility.HtmlDecode(MarkdownPageFixture.Html(page.Children));

    [Fact]
    public async Task WhenArgumentsUseThePropertyNames_ThenEveryKindOfValueIsSet()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md", $":: {LeafType}#leaf Name=\"Text Value\" Count=5 Enabled=true Interval=00:00:01.500\n::");

        // Assert
        var leaf = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Equal("Text Value", leaf.Name);
        Assert.Equal(5, leaf.Count);
        Assert.True(leaf.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), leaf.Interval);
    }

    [Theory]
    [InlineData("name=x", "name", "Name")]
    [InlineData("count=5", "count", "Count")]
    [InlineData("COUNT=5", "COUNT", "Count")]
    public async Task WhenArgumentKeyDiffersFromAPropertyNameOnlyInCase_ThenTheErrorNamesTheProperty(
        string argument, string key, string propertyName)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $":: {LeafType}#leaf {argument}\n::");

        // Assert
        Assert.False(page.Children.ContainsKey("leaf"));
        Assert.Contains(
            $"TestLeaf has no configuration property '{key}'. Did you mean '{propertyName}'? " +
            "Available: Count, Enabled, Interval, Name, Tags.",
            DecodedHtml(page));
    }

    [Fact]
    public async Task WhenArgumentKeyIsNoPropertyNameInAnyCase_ThenTheErrorHasNoHint()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $":: {LeafType}#leaf Cont=5\n::");

        // Assert
        Assert.Contains(
            "TestLeaf has no configuration property 'Cont'. Available: Count, Enabled, Interval, Name, Tags.",
            DecodedHtml(page));
    }

    [Fact]
    public async Task WhenSlotBlockArgumentKeyIsInCamelCase_ThenTheErrorNamesTheProperty()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $":: {PanelType}#panel\n::: Row#row label=A\n:::\n::");

        // Assert
        Assert.Contains(
            "TestRow has no configuration property 'label'. Did you mean 'Label'? Available: Label.",
            DecodedHtml(page));
    }

    [Fact]
    public async Task WhenJsonBlockUsesThePropertyNames_ThenTheyAreReadAtEveryDepth()
    {
        // Arrange
        var fixture = await CreateFixtureWithComponentsAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", GridPage);

        // Assert
        var grid = Assert.IsType<GridLayout>(page.Children["grid"]);
        Assert.Equal(1, grid.Rows);
        Assert.Equal(2, grid.Columns);

        var cell = Assert.Single(grid.Cells);
        Assert.Equal(0, cell.Row);
        Assert.Equal(1, cell.Column);
        Assert.Equal(2, cell.ColumnSpan);

        var widget = Assert.IsType<Widget>(cell.Child);
        Assert.Equal("../Compressor", widget.Path);
    }

    [Fact]
    public async Task WhenTopLevelKeyOfJsonBlockIsInCamelCase_ThenTheErrorNamesTheProperty()
    {
        // Arrange
        var fixture = await CreateFixtureWithComponentsAsync();

        // Act
        var page = await fixture.AddPageAsync(
            "page.md", $":: {GridType}#grid Rows=1\n```json\n{{ \"cells\": [] }}\n```\n::");

        // Assert
        Assert.False(page.Children.ContainsKey("grid"));
        Assert.Contains(
            "GridLayout has no configuration property 'cells'. Did you mean 'Cells'? Available: Cells, Columns, Rows.",
            DecodedHtml(page));
    }

    // Only the top level of the json block is checked against the type. Below it a key that names no
    // property is skipped like in a configuration file, and a key in camelCase is such a key.
    [Fact]
    public async Task WhenNestedKeyOfJsonBlockIsInCamelCase_ThenThatKeyIsSkippedWithoutAnError()
    {
        // Arrange
        var fixture = await CreateFixtureWithComponentsAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $$"""
            :: {{GridType}}#grid
            ```json
            {
              "Cells": [
                { "row": 3, "Column": 1, "Child": { "$type": "{{WidgetType}}", "path": "../Compressor" } }
              ]
            }
            ```
            ::
            """);

        // Assert
        Assert.DoesNotContain("markdown-block-error", MarkdownPageFixture.Html(page.Children));

        var cell = Assert.Single(Assert.IsType<GridLayout>(page.Children["grid"]).Cells);
        Assert.Null(cell.Row);
        Assert.Equal(1, cell.Column);
        Assert.Equal(string.Empty, Assert.IsType<Widget>(cell.Child).Path);
    }

    [Fact]
    public async Task WhenListIsWrittenBack_ThenTheJsonBlockIsWrittenWithThePropertyNames()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $"intro\n\n:: {LeafType}#leaf Count=1\n::\n\noutro\n");
        var leaf = (TestLeaf)page.Children["leaf"];
        leaf.Tags = ["a", "b"];

        // Act
        var handled = await page.WriteConfigurationAsync(leaf, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            $"intro\n\n:: {LeafType}#leaf Count=1 Enabled=false Interval=00:00:00\n" +
            "```json\n{\n  \"Tags\": [\n    \"a\",\n    \"b\"\n  ]\n}\n```\n::\n\noutro\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenBlockWithNestedSubjectsIsWrittenBack_ThenEveryDepthIsWrittenWithThePropertyNames()
    {
        // Arrange
        var fixture = await CreateFixtureWithComponentsAsync();
        var page = await fixture.AddPageAsync("page.md", GridPage);
        var grid = (GridLayout)page.Children["grid"];
        grid.Columns = 3;

        // Act
        var handled = await page.WriteConfigurationAsync(grid, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(GridPage.Replace("Columns=2", "Columns=3"), await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenSubjectNestedInTheJsonBlockIsWrittenBack_ThenItsBlockIsWrittenWithThePropertyNames()
    {
        // Arrange
        var fixture = await CreateFixtureWithComponentsAsync();
        var page = await fixture.AddPageAsync("page.md", GridPage);
        var widget = (Widget)((GridLayout)page.Children["grid"]).Cells[0].Child!;
        widget.Path = "../CoolingFan";

        // Act
        var handled = await page.WriteConfigurationAsync(widget, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(GridPage.Replace("../Compressor", "../CoolingFan"), await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenTypedSlotValueIsWrittenBack_ThenItsHeadLineIsWrittenWithThePropertyNames()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync(
            "page.md", $":: {PanelType}#panel Title=Panel\n::: Row#row Label=A\ntext\n:::\n::\n");
        var row = Assert.Single(((TestPanel)page.Children["panel"]).Rows);
        row.Label = "New Label";

        // Act
        var handled = await page.WriteConfigurationAsync(row, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            $":: {PanelType}#panel Title=Panel\n::: Row#row Label=\"New Label\"\ntext\n:::\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenPageIsLoadedAgainAfterAWriteBack_ThenTheInstanceIsKept()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {LeafType}#leaf Name=x Count=1\n::\n");
        var leaf = (TestLeaf)page.Children["leaf"];
        leaf.Name = null;
        leaf.Tags = ["a"];
        await page.WriteConfigurationAsync(leaf, CancellationToken.None);

        // Act
        await page.OnFileChangedAsync(CancellationToken.None);

        // Assert
        Assert.Same(leaf, page.Children["leaf"]);
        Assert.Equal(["a"], leaf.Tags);
    }

    [Fact]
    public async Task WhenKeyThatAWriteBackWroteIsRemovedFromThePage_ThenANewInstanceIsCreated()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $":: {LeafType}#leaf Count=1\n::\n");
        var leaf = (TestLeaf)page.Children["leaf"];
        leaf.Name = "x";
        await page.WriteConfigurationAsync(leaf, CancellationToken.None);
        var written = await fixture.ReadAsync("page.md");

        // Act
        await fixture.UpdatePageAsync(page, written.Replace(" Name=x", string.Empty));

        // Assert
        Assert.Contains(" Name=x", written);
        var reloaded = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.NotSame(leaf, reloaded);
        Assert.Null(reloaded.Name);
    }

    [Fact]
    public async Task WhenBlockIsCreatedForANewSubject_ThenItIsWrittenWithThePropertyNames()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var serializer = fixture.Services.GetRequiredService<ConfigurableSubjectSerializer>();
        var leaf = new TestLeaf { Name = "New Leaf", Count = 3, Tags = ["a"] };

        // Act
        var block = MarkdownBlockWriter.CreateBlock(
            LeafType, "leaf", serializer.Serialize(leaf, ConfigurationNaming.PropertyName), fenceLength: 2);
        var page = await fixture.AddPageAsync("page.md", block);

        // Assert
        Assert.Equal(
            $":: {LeafType}#leaf Name=\"New Leaf\" Count=3 Enabled=false Interval=00:00:00\n" +
            "```json\n{\n  \"Tags\": [\n    \"a\"\n  ]\n}\n```\n::",
            block);

        var created = Assert.IsType<TestLeaf>(page.Children["leaf"]);
        Assert.Equal("New Leaf", created.Name);
        Assert.Equal(3, created.Count);
        Assert.Equal(["a"], created.Tags);
    }
}
