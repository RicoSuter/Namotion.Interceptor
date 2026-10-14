using System.Net;
using HomeBlaze.Services;
using HomeBlaze.Storage.Pages;
using Microsoft.Extensions.DependencyInjection;
using static HomeBlaze.Storage.Tests.Pages.TestTypeNames;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownSlotConfigurationTests
{
    [Fact]
    public async Task WhenSlotPropertyIsAlsoConfiguration_ThenBlocksFillItOnAPage()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var content =
            $":: {BoardType}#board Columns=2\n" +
            "::: Tile Position=1\nfirst **text**\n:::\n" +
            $"::: Tile Position=2\n:::: {LeafType}#leaf Count=7\n::::\n:::\n" +
            "::: Tile Position=3\n:::\n" +
            "::\n";

        // Act
        var page = await fixture.AddPageAsync("page.md", content);

        // Assert
        var board = Assert.IsType<TestBoard>(page.Children["board"]);
        Assert.Equal(2, board.Columns);
        Assert.Equal([1, 2, 3], board.Tiles.Select(tile => tile.Position));
        Assert.Contains("first", MarkdownPageFixture.Html(Assert.IsType<MarkdownFragment>(board.Tiles[0].Child).Children));
        var leaf = Assert.IsType<MarkdownFragment>(board.Tiles[1].Child).Children.Values.OfType<TestLeaf>().Single();
        Assert.Equal(7, leaf.Count);
        Assert.Null(board.Tiles[2].Child);
    }

    [Theory]
    [InlineData(
        ":: {0} Tiles=1\n::",
        "Line 1: 'Tiles' is written as blocks on a page: one 'Tile' block per value inside this block.")]
    [InlineData(
        ":: {0}\n```json\n{{ \"Tiles\": [ {{ \"Position\": 1 }} ] }}\n```\n::",
        "Line 1: 'Tiles' is written as blocks on a page: one 'Tile' block per value inside this block.")]
    [InlineData(
        ":: {0}\n```json\n{{ \"tiles\": [] }}\n```\n::",
        "Line 1: 'Tiles' is written as blocks on a page: one 'Tile' block per value inside this block.")]
    [InlineData(
        ":: {0}\n::: Tile Child=x\n:::\n::",
        "Line 2: 'Child' is the content of the block on a page, so it cannot be set as an argument.")]
    [InlineData(
        ":: {0}\n::: Tile\n```json\n{{ \"Child\": {{ \"$type\": \"HomeBlaze.Storage.Tests.Pages.TestLeaf\" }} }}\n```\n:::\n::",
        "Line 2: 'Child' is the content of the block on a page, so it cannot be set as an argument.")]
    public async Task WhenSlotPropertyIsSetAsConfigurationOnAPage_ThenTheBlockIsAnError(string format, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", string.Format(format, BoardType));

        // Assert
        Assert.Contains(expectedMessage, WebUtility.HtmlDecode(MarkdownPageFixture.Html(page.Children)));
        Assert.DoesNotContain(page.Children.Values, child => child is TestBoard);
    }

    [Theory]
    [InlineData("Rows=1", "Line 1: 'Rows' is written as blocks on a page: one 'Row' block per value inside this block.")]
    [InlineData("Footer=x", "Line 1: 'Footer' is written as a block on a page: a 'Footer' block inside this block.")]
    [InlineData("Content=x", "Line 1: 'Content' is the content of the block on a page, so it cannot be set as an argument.")]
    public async Task WhenPropertyThatIsOnlyASlotIsSetAsArgument_ThenTheMessageSaysHowToWriteIt(string argument, string expectedMessage)
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();

        // Act
        var page = await fixture.AddPageAsync("page.md", $":: {Namespace}.TestPanel {argument}\n::");

        // Assert
        Assert.Contains(expectedMessage, WebUtility.HtmlDecode(MarkdownPageFixture.Html(page.Children)));
    }

    [Fact]
    public async Task WhenSubjectIsReadFromAFile_ThenTheSlotPropertiesAreConfiguration()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var serializer = fixture.Services.GetRequiredService<ConfigurableSubjectSerializer>();
        var json =
            $$"""
            {
              "$type": "{{BoardType}}",
              "columns": 3,
              "tiles": [
                { "position": 1, "child": { "$type": "{{LeafType}}", "count": 4 } }
              ]
            }
            """;

        // Act
        var board = Assert.IsType<TestBoard>(serializer.Deserialize(json));
        var written = serializer.Serialize(board);

        // Assert
        Assert.Equal(4, Assert.IsType<TestLeaf>(Assert.Single(board.Tiles).Child).Count);
        Assert.Contains("\"tiles\"", written);
        Assert.Contains("\"count\": 4", written);
    }
}
