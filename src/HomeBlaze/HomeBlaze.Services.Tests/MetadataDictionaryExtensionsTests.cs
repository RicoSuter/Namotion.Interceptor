using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;

namespace HomeBlaze.Services.Tests;

public class MetadataDictionaryExtensionsTests
{
    private static ImmutableDictionary<string, JsonElement> Metadata(string json) => TestMetadata.Parse(json);

    [Fact]
    public void WhenKnownKeysHaveTheirShape_ThenTheyAreReadWithoutRegardToCase()
    {
        // Arrange
        var metadata = Metadata("""{ "NavTitle": "Pump", "location": "appBar", "alignment": "Right", "order": ["b", "a"] }""");

        // Act
        var navigationTitle = metadata.GetString(MetadataKeys.NavigationTitle);
        var location = metadata.GetEnum<NavigationLocation>(MetadataKeys.Location);
        var alignment = metadata.GetEnum<AppBarAlignment>(MetadataKeys.Alignment);
        var order = metadata.GetStringArray(MetadataKeys.Order);

        // Assert
        Assert.Equal("Pump", navigationTitle);
        Assert.Equal(NavigationLocation.AppBar, location);
        Assert.Equal(AppBarAlignment.Right, alignment);
        Assert.Equal(["b", "a"], order.ToArray());
    }

    [Fact]
    public void WhenKnownKeysHaveAnotherShape_ThenTheyAreNotSet()
    {
        // Arrange
        var metadata = Metadata("""{ "title": " ", "navTitle": 5, "icon": "\ud800", "location": 1, "alignment": "Up", "order": ["a", null, 2] }""");

        // Act
        var title = metadata.GetString(MetadataKeys.Title);
        var navigationTitle = metadata.GetString(MetadataKeys.NavigationTitle);
        var icon = metadata.GetString(MetadataKeys.Icon);
        var location = metadata.GetEnum<NavigationLocation>(MetadataKeys.Location);
        var alignment = metadata.GetEnum<AppBarAlignment>(MetadataKeys.Alignment);
        var order = metadata.GetStringArray(MetadataKeys.Order);

        // Assert
        Assert.Null(title);
        Assert.Null(navigationTitle);
        Assert.Null(icon);
        Assert.Null(location);
        Assert.Null(alignment);
        Assert.Equal(["a"], order.ToArray());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("5")]
    [InlineData("+1")]
    [InlineData(" 1")]
    [InlineData("Left, Right")]
    [InlineData("Left ")]
    [InlineData("")]
    public void WhenTheTextIsNoNameOfTheEnum_ThenItIsNotParsed(string text)
    {
        // Act
        var alignment = MetadataDictionaryExtensions.ParseEnumName<AppBarAlignment>(text);

        // Assert
        Assert.Null(alignment);
    }

    [Fact]
    public void WhenThereIsNoMetadata_ThenNoKeyIsSet()
    {
        // Arrange
        ImmutableDictionary<string, JsonElement>? metadata = null;

        // Act
        var title = metadata.GetString(MetadataKeys.Title);
        var order = metadata.GetStringArray(MetadataKeys.Order);

        // Assert
        Assert.Null(title);
        Assert.Empty(order.ToArray());
    }

    [Fact]
    public void WhenLinksAreRead_ThenOnlyLinksWithExactlyOneTargetAreReturned()
    {
        // Arrange
        var metadata = Metadata("""
            {
              "links": [
                { "title": "Manual", "url": "https://example.com/manual.pdf" },
                { "PATH": "/Devices/Pump" },
                { "title": "Neither" },
                { "url": "https://example.com", "path": "/Devices" },
                "https://example.com"
              ]
            }
            """);

        // Act
        var links = metadata.GetLinks(MetadataKeys.Links);

        // Assert
        Assert.Equal(
            [new MetadataLink("Manual", "https://example.com/manual.pdf", null), new MetadataLink(null, null, "/Devices/Pump")],
            links.ToArray());
    }

    [Fact]
    public void WhenALinkWasEditedByHandToAnAddressThatIsNotAllowed_ThenItIsReadAsNotAllowed()
    {
        // Arrange
        var metadata = Metadata("""{ "links": [{ "title": "Run", "url": "javascript:alert(1)" }] }""");

        // Act
        var link = Assert.Single(metadata.GetLinks(MetadataKeys.Links));

        // Assert
        Assert.Equal("javascript:alert(1)", link.Url);
        Assert.False(MetadataLink.IsAllowedAddress(link.Url!));
    }

    [Fact]
    public void WhenAFileHoldsMoreLinksThanMayBeWritten_ThenOnlyTheFirstTwentyAreRead()
    {
        // Arrange
        var items = Enumerable.Range(0, 1000).Select(index => $$"""{ "path": "/Devices/{{index}}" }""");
        var metadata = Metadata($$"""{ "links": [{{string.Join(", ", items)}}] }""");

        // Act
        var links = metadata.GetLinks(MetadataKeys.Links);

        // Assert
        Assert.Equal(20, links.Length);
        Assert.Equal("/Devices/19", links[^1].Path);
    }
}
