using System.Text;
using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataDocumentTests
{
    [Fact]
    public void WhenFileIsParsed_ThenKnownAndUnknownKeysAreRead()
    {
        // Arrange
        var content = Encoding.UTF8.GetBytes("""
            {
              // A comment and a trailing comma are allowed.
              "title": "Devices",
              "navTitle": "Devs",
              "icon": "Devices",
              "location": "appBar",
              "alignment": "Right",
              "order": ["Readme.md", "Pumps"],
              "color": "red",
            }
            """);

        // Act
        var document = StorageMetadataDocument.Parse(content);

        // Assert
        Assert.Equal("Devices", document.Title);
        Assert.Equal("Devs", document.NavigationTitle);
        Assert.Equal("Devices", document.Icon);
        Assert.Equal(NavigationLocation.AppBar, document.Location);
        Assert.Equal(AppBarAlignment.Right, document.Alignment);
        string?[] expectedOrder = ["Readme.md", "Pumps"];
        Assert.Equal(expectedOrder, document.Order);
        Assert.True(document.ExtensionData!.ContainsKey("color"));
    }

    [Fact]
    public void WhenFileStartsWithByteOrderMark_ThenItIsParsed()
    {
        // Arrange
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{ "title": "Docs" }""")).ToArray();

        // Act
        var document = StorageMetadataDocument.Parse(content);

        // Assert
        Assert.Equal("Docs", document.Title);
    }

    [Fact]
    public void WhenDocumentIsSerialized_ThenUnknownKeysAreKeptAndNullsAreLeftOut()
    {
        // Arrange
        var document = StorageMetadataDocument.Parse(Encoding.UTF8.GetBytes("""{ "color": "red" }"""));
        document.Location = NavigationLocation.AppBar;

        // Act
        var json = Encoding.UTF8.GetString(document.Serialize());

        // Assert
        Assert.Contains("\"color\": \"red\"", json);
        Assert.Contains("\"location\": \"AppBar\"", json);
        Assert.DoesNotContain("title", json);
        Assert.DoesNotContain("isEmpty", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WhenContentIsNotJson_ThenParsingFails()
    {
        // Act & Assert
        Assert.ThrowsAny<JsonException>(() => StorageMetadataDocument.Parse(Encoding.UTF8.GetBytes("not json")));
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{ "order": [] }""", false)]
    [InlineData("""{ "color": "red" }""", false)]
    [InlineData("""{ "icon": "Devices" }""", false)]
    public void WhenDocumentIsChecked_ThenOnlyADocumentWithoutAnyKeyIsEmpty(string json, bool expected)
    {
        // Arrange
        var document = StorageMetadataDocument.Parse(Encoding.UTF8.GetBytes(json));

        // Act
        var isEmpty = document.IsEmpty;

        // Assert
        Assert.Equal(expected, isEmpty);
    }

    [Fact]
    public void WhenValuesAreBlank_ThenTheyAreNotSetInTheMetadata()
    {
        // Arrange
        var document = StorageMetadataDocument.Parse(Encoding.UTF8.GetBytes("""{ "title": " ", "navTitle": "", "order": ["a", null] }"""));

        // Act
        var folder = document.ToFolderMetadata();
        var subject = document.ToSubjectMetadata();
        var order = document.ToChildOrder();

        // Assert
        Assert.Null(folder);
        Assert.Null(subject);
        Assert.Equal(["a"], order.AsSpan().ToArray());
    }

    [Fact]
    public void WhenEnumValueIsUndefined_ThenItIsNotSetInTheMetadata()
    {
        // Arrange
        var document = StorageMetadataDocument.Parse(Encoding.UTF8.GetBytes("""{ "navTitle": "Pump", "location": 5, "alignment": 7 }"""));

        // Act
        var subject = document.ToSubjectMetadata();

        // Assert
        Assert.Equal(new SubjectMetadata("Pump", null, null, null), subject);
    }
}
