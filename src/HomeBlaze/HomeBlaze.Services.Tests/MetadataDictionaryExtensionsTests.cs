using System.Collections.Immutable;
using System.Text;
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
        var links = metadata.GetLinks();

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
        var link = Assert.Single(metadata.GetLinks());

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
        var links = metadata.GetLinks();

        // Assert
        Assert.Equal(20, links.Length);
        Assert.Equal("/Devices/19", links[^1].Path);
    }

    [Fact]
    public void WhenTagsWereEditedByHand_ThenOnlyTagsThatMayBeWrittenAreReadTrimmedAndOnce()
    {
        // Arrange
        var metadata = Metadata($$"""
            {
              "tags": ["", " ", " pump ", "PUMP", "{{new string('a', 65)}}", "a\u202Eb", "a\u200Bb", "a\tb", 5, "floor-2"]
            }
            """);

        // Act
        var tags = metadata.GetTags();

        // Assert
        Assert.Equal(["pump", "floor-2"], tags.ToArray());
    }

    [Fact]
    public void WhenAFileHoldsMoreTagsThanMayBeWritten_ThenOnlyTheFirstTwentyAreRead()
    {
        // Arrange
        var items = Enumerable.Range(0, 1000).Select(index => $"\"tag{index}\"");
        var metadata = Metadata($$"""{ "tags": [{{string.Join(", ", items)}}] }""");

        // Act
        var tags = metadata.GetTags();

        // Assert
        Assert.Equal(20, tags.Length);
        Assert.Equal("tag19", tags[^1]);
    }

    [Fact]
    public void WhenTheMembersOfALinkHaveWhitespaceAroundThem_ThenTheyAreReadTrimmed()
    {
        // Arrange
        var metadata = Metadata("""{ "links": [{ "title": " Manual ", "url": " https://example.com/manual.pdf" }, { "path": "\t/Devices/Pump " }] }""");

        // Act
        var links = metadata.GetLinks();

        // Assert
        Assert.Equal(
            [new MetadataLink("Manual", "https://example.com/manual.pdf", null), new MetadataLink(null, null, "/Devices/Pump")],
            links.ToArray());
    }

    [Fact]
    public void WhenTheTitleOfALinkHasAControlOrFormatCharacter_ThenTheLinkIsReadWithoutTitle()
    {
        // Arrange
        var metadata = Metadata("""{ "links": [{ "title": "Manual\u202Efdp.exe", "url": "https://example.com" }, { "title": "A\u0007", "path": "/Devices" }] }""");

        // Act
        var links = metadata.GetLinks();

        // Assert
        Assert.Equal([new MetadataLink(null, "https://example.com", null), new MetadataLink(null, null, "/Devices")], links.ToArray());
    }

    [Fact]
    public void WhenTheAddressOfALinkHasAControlOrFormatCharacter_ThenTheLinkIsNotRead()
    {
        // Arrange
        var metadata = Metadata("""{ "links": [{ "url": "https://example.com/\u202Efdp.exe" }, { "path": "/Devices/\u200BPump" }, { "path": "/Devices" }] }""");

        // Act
        var links = metadata.GetLinks();

        // Assert
        Assert.Equal([new MetadataLink(null, null, "/Devices")], links.ToArray());
    }

    [Fact]
    public void WhenALinkHasAMemberWhoseNameCannotBeRead_ThenTheLinkIsReadWithoutIt()
    {
        // Arrange
        var metadata = Metadata("""{ "links": [{ "\uD800": "x", "\u0055rl": "https://example.com" }] }""");

        // Act
        var links = metadata.GetLinks();

        // Assert
        Assert.Equal([new MetadataLink(null, "https://example.com", null)], links.ToArray());
    }

    [Theory]
    [InlineData("""{ "notes": " ", "links": [{ "url": 1 }, { "title": "Neither" }, { "path": "/Pump\u202E" }], "tags": [5, " ", "a\u200Bb"], "comment": "x" }""", false)]
    [InlineData("""{ "notes": "Calibrated" }""", true)]
    [InlineData("""{ "links": [{ "path": "/Devices/Pump" }] }""", true)]
    [InlineData("""{ "tags": ["pump"] }""", true)]
    public void WhenMetadataIsCheckedForNotesLinksOrTags_ThenOnlyThoseThatAreReadCount(string json, bool expected)
    {
        // Act
        var hasNotesLinksOrTags = Metadata(json).HasNotesLinksOrTags();

        // Assert
        Assert.Equal(expected, hasNotesLinksOrTags);
    }

    // JSON string content, with raw characters and with escapes.
    public static TheoryData<string> StringValues =>
    [
        "", " ", "\u00A0\u3000", "\\u0020", "\\t", "pump", " pump ", "\\u0070ump", "\u00E9t\u00E9", "\U0001F600",
        "a\u200Bb", "a\u202Eb", "pump\U000E0041", "a\\u200Bb", "a\\tb", "\\uD800", "\\uD800 pump",
        new string('a', 64), new string('a', 65), " " + new string('a', 64) + "\u3000", string.Concat(Enumerable.Repeat("\U0001F600", 32)),
        string.Concat(Enumerable.Repeat("\U0001F600", 33)), "\\/Devices\\/Pump", "https://example.com/manual.pdf"
    ];

    [Theory]
    [MemberData(nameof(StringValues))]
    public void WhenMetadataIsCheckedForNotesLinksOrTags_ThenItAgreesWithWhatIsRead(string value)
    {
        // Arrange
        var notes = Metadata($$"""{ "notes": "{{value}}" }""");
        var tags = Metadata($$"""{ "tags": ["{{value}}"] }""");
        var urls = Metadata($$"""{ "links": [{ "title": "Manual", "URL": "{{value}}" }] }""");
        var paths = Metadata($$"""{ "links": [{ "\u0070ath": 1, "path": "{{value}}" }] }""");
        var urlsAndPaths = Metadata($$"""{ "links": [{ "url": "{{value}}", "path": "/Devices" }] }""");

        // Act & Assert
        Assert.Equal(notes.GetString(MetadataKeys.Notes) is not null, notes.HasNotesLinksOrTags());
        Assert.Equal(tags.GetTags().Length > 0, tags.HasNotesLinksOrTags());
        Assert.Equal(urls.GetLinks().Length > 0, urls.HasNotesLinksOrTags());
        Assert.Equal(paths.GetLinks().Length > 0, paths.HasNotesLinksOrTags());
        Assert.Equal(urlsAndPaths.GetLinks().Length > 0, urlsAndPaths.HasNotesLinksOrTags());
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("tags")]
    [InlineData("url")]
    public void WhenAStringIsNoValidUtf8_ThenItIsNoNotesLinkOrTag(string key)
    {
        // Arrange
        var json = key switch
        {
            "notes" => "{ \"notes\": \"pump \u0001\" }",
            "tags" => "{ \"tags\": [\"pump \u0001\"] }",
            _ => "{ \"links\": [{ \"url\": \"https://example.com/\u0001\" }] }"
        };
        var utf8 = Encoding.UTF8.GetBytes(json);
        utf8[Array.IndexOf(utf8, (byte)1)] = 0xC3;
        var metadata = JsonDocument.Parse(utf8).RootElement.EnumerateObject().ToImmutableDictionary(property => property.Name, property => property.Value.Clone());

        // Act
        var hasNotesLinksOrTags = metadata.HasNotesLinksOrTags();

        // Assert
        Assert.False(hasNotesLinksOrTags);
        Assert.Null(metadata.GetString(MetadataKeys.Notes));
        Assert.Empty(metadata.GetTags());
        Assert.Empty(metadata.GetLinks());
    }

    [Fact]
    public void WhenTheSameMetadataIsCheckedAgainForNotesLinksOrTags_ThenNothingIsAllocated()
    {
        // Arrange
        var notes = Metadata("""{ "notes": " Calibrated in 2026, see the manual." }""");
        var tags = Metadata("""{ "tags": [" ", "pump"] }""");
        var links = Metadata("""{ "links": [{ "title": "Manual", "URL": "https://example.com/manual.pdf" }] }""");
        var none = Metadata("""{ "notes": " ", "tags": [" "], "links": [{ "title": "Neither" }] }""");
        bool Check() => notes.HasNotesLinksOrTags() && tags.HasNotesLinksOrTags() && links.HasNotesLinksOrTags() && !none.HasNotesLinksOrTags();
        var firstResult = Check();

        // Act
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = Check();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        Assert.True(firstResult);
        Assert.True(result);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WhenAMutableDictionaryChanges_ThenTheCheckForNotesLinksOrTagsSeesTheChange()
    {
        // Arrange
        var metadata = new Dictionary<string, JsonElement>();
        var hadNotes = metadata.HasNotesLinksOrTags();

        // Act
        metadata[MetadataKeys.Notes] = JsonElement.Parse("\"Calibrated\"");
        var hasNotes = metadata.HasNotesLinksOrTags();

        // Assert
        Assert.False(hadNotes);
        Assert.True(hasNotes);
    }
}
