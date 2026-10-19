using System.Text.Json;
using HomeBlaze.Components;
using HomeBlaze.Storage.Pages;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownBlockConfigurationTests
{
    private const string Defaults = """{ "$type": "T", "Outlined": true, "Elevation": 2, "Title": null, "Items": [1, 2], "Style": { "Color": "red" } }""";

    private static readonly HashSet<string> NoKeys = [];

    [Fact]
    public void WhenValueEqualsTheDefault_ThenItIsOmitted()
    {
        // Arrange
        const string configuration = """{ "$type": "T", "Title": "Card", "Outlined": true, "Elevation": 2 }""";

        // Act
        var result = OmitDefaults(configuration, NoKeys);

        // Assert
        Assert.Equal(["$type", "Title"], GetKeys(result));
    }

    [Fact]
    public void WhenKeyIsKept_ThenItsDefaultValueIsWritten()
    {
        // Arrange
        const string configuration = """{ "$type": "T", "Outlined": true, "Elevation": 2 }""";

        // Act
        var result = OmitDefaults(configuration, ["Outlined"]);

        // Assert
        Assert.Equal(["$type", "Outlined"], GetKeys(result));
    }

    [Fact]
    public void WhenValueDiffersFromTheDefault_ThenItIsWritten()
    {
        // Arrange
        const string configuration = """{ "$type": "T", "Outlined": false, "Elevation": 4, "Width": 3 }""";

        // Act
        var result = OmitDefaults(configuration, NoKeys);

        // Assert
        Assert.Same(configuration, result);
    }

    [Fact]
    public void WhenOnePartOfAListOrAnObjectDiffersFromTheDefault_ThenTheWholeValueIsWritten()
    {
        // Arrange
        const string configuration = """{ "$type": "T", "Items": [1, 3], "Style": { "Color": "blue" } }""";

        // Act
        var result = OmitDefaults(configuration, NoKeys);

        // Assert
        Assert.Same(configuration, result);
    }

    [Fact]
    public void WhenListAndObjectEqualTheDefault_ThenTheyAreOmitted()
    {
        // Arrange
        const string configuration = """{ "$type": "T", "Items": [1, 2], "Style": { "Color": "red" } }""";

        // Act
        var result = OmitDefaults(configuration, NoKeys);

        // Assert
        Assert.Equal(["$type"], GetKeys(result));
    }

    [Fact]
    public async Task WhenCardIsWrittenWithoutChanges_ThenItsPageStaysTheSame()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        const string content = ":: HomeBlaze.Components.Card#card Title=\"Current temperatures\" Icon=Thermostat\ntext\n::\n";
        var page = await fixture.AddPageAsync("page.md", content);
        var card = Assert.IsType<Card>(page.Children["card"]);

        // Act
        var handled = await page.WriteConfigurationAsync(card, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(content, await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenCardHasADefaultValueWrittenOnPurpose_ThenWritingKeepsItAndAddsNoOtherDefault()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync(
            "page.md",
            ":: HomeBlaze.Components.Card#card Title=Old Outlined=true\n```json\n{ \"Elevation\": 2 }\n```\ntext\n::\n");
        var card = Assert.IsType<Card>(page.Children["card"]);
        card.Title = "New";
        card.Icon = "Thermostat";

        // Act
        var handled = await page.WriteConfigurationAsync(card, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            ":: HomeBlaze.Components.Card#card Title=New Icon=Thermostat Outlined=true Elevation=2\ntext\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    [Fact]
    public async Task WhenCardValueIsChangedFromItsDefault_ThenItIsWritten()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", ":: HomeBlaze.Components.Card#card Title=Old\ntext\n::\n");
        var card = Assert.IsType<Card>(page.Children["card"]);
        card.Outlined = false;
        card.Elevation = 4;

        // Act
        var handled = await page.WriteConfigurationAsync(card, CancellationToken.None);

        // Assert
        Assert.True(handled);
        Assert.Equal(
            ":: HomeBlaze.Components.Card#card Title=Old Outlined=false Elevation=4\ntext\n::\n",
            await fixture.ReadAsync("page.md"));
    }

    private static string OmitDefaults(string configuration, HashSet<string> keptKeys)
    {
        using var defaults = JsonDocument.Parse(Defaults);
        return MarkdownBlockConfiguration.OmitDefaults(configuration, defaults.RootElement, keptKeys);
    }

    private static string[] GetKeys(string json)
    {
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateObject().Select(property => property.Name)];
    }
}
