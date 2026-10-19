using System.Collections.Immutable;
using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;

namespace HomeBlaze.Services.Tests;

public class MetadataDictionaryExtensionsTests
{
    private static ImmutableDictionary<string, JsonElement> Metadata(string json)
        => JsonElement.Parse(json).EnumerateObject()
            .ToImmutableDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);

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
}
