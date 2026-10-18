using System.Text;
using System.Text.Json;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataDocumentTests
{
    private static StorageMetadataDocument Parse(string json)
        => StorageMetadataDocument.TryParse(Encoding.UTF8.GetBytes(json), out var document, out var error) ? document : throw error;

    private static JsonElement Values(string json) => JsonElement.Parse(json);

    private static string Write(StorageMetadataDocument document) => Encoding.UTF8.GetString(document.Serialize());

    private static string JsonObject(IEnumerable<string> members) => "{" + string.Join(", ", members) + "}";

    // An array of strings of 200 characters, which as a JSON value takes about 203 bytes each.
    private static string LongStrings(int count) => "[" + string.Join(", ", Enumerable.Repeat($"\"{new string('a', 200)}\"", count)) + "]";

    // The document, the values merged into it, and a part of the message.
    public static TheoryData<string, string, string> ValuesOverALimit() => new()
    {
        { "{}", $$"""{ "{{new string('k', 65)}}": 1 }""", "64 characters" },
        { "{}", $$"""{ "note": "{{new string('a', 257)}}" }""", "'note'" },
        { "{}", $$"""{ "notes": {{LongStrings(21)}} }""", "'notes'" },
        { "{}", JsonObject(Enumerable.Range(0, 65).Select(index => $"\"key{index}\": null")), "64 keys" },
        { JsonObject(Enumerable.Range(0, 64).Select(index => $"\"key{index}\": {index}")), """{ "extra": 1 }""", "'extra'" },
        { "{}", JsonObject(Enumerable.Range(0, 5).Select(index => $"\"part{index}\": {LongStrings(17)}")), "16 KB with 'part0', 'part1', 'part2', 'part3', 'part4'" }
    };

    public static TheoryData<string, string> ValuesWithAControlCharacterInAKey() => new()
    {
        { """{ "a\r\nb": 1, "A\r\nB": 2 }""", @"'A\u000D\u000AB'" },
        { $$"""{ "a\r\nb": "{{new string('x', 257)}}" }""", @"'a\u000D\u000Ab'" }
    };

    [Fact]
    public void WhenFileIsParsed_ThenKnownKeysAreReadWithoutRegardToCase()
    {
        // Arrange
        const string json = """
            {
              // A comment and a trailing comma are allowed.
              "Title": "Devices",
              "navTitle": "Devs",
              "ICON": "Devices",
              "location": "appBar",
              "alignment": "Right",
              "order": ["Readme.md", "Pumps"],
            }
            """;

        // Act
        var document = Parse(json);

        // Assert
        Assert.Equal(new FolderMetadata { Title = "Devices", NavigationTitle = "Devs", Icon = "Devices" }, document.FolderMetadata);
        Assert.Equal(
            new SubjectMetadata { NavigationTitle = "Devs", Icon = "Devices", Location = NavigationLocation.AppBar, Alignment = AppBarAlignment.Right },
            document.SubjectMetadata);
        Assert.Equal(["Readme.md", "Pumps"], document.ChildOrder.ToArray());
    }

    [Fact]
    public void WhenFileStartsWithByteOrderMark_ThenItIsParsed()
    {
        // Arrange
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{ "title": "Docs" }""")).ToArray();

        // Act
        var isParsed = StorageMetadataDocument.TryParse(content, out var document, out _);

        // Assert
        Assert.True(isParsed);
        Assert.Equal("Docs", document!.FolderMetadata?.Title);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{ \"a\": 1, \"A\": 2 }")]
    [InlineData("{ \"\\ud800\": 1 }")]
    public void WhenContentIsNoJsonObjectOrHasAKeyThatCannotBeRead_ThenItIsNoMetadata(string json)
    {
        // Act
        var isParsed = StorageMetadataDocument.TryParse(Encoding.UTF8.GetBytes(json), out var document, out var error);

        // Assert
        Assert.False(isParsed);
        Assert.Null(document);
        Assert.NotNull(error);
    }

    [Fact]
    public void WhenDocumentIsWritten_ThenItIsIndentedEndsWithANewLineKeepsNonAsciiAndLeavesOutNulls()
    {
        // Arrange
        var document = Parse("""{ "title": "Geräte", "note": null, "tags": { "room": "Küche" } }""");

        // Act
        var text = Write(document);

        // Assert
        Assert.Equal("{\n  \"title\": \"Geräte\",\n  \"tags\": {\n    \"room\": \"Küche\"\n  }\n}\n", text);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{ "title": null, "tags": null }""", true)]
    [InlineData("""{ "order": [] }""", false)]
    [InlineData("""{ "color": "red" }""", false)]
    public void WhenDocumentIsChecked_ThenOnlyADocumentWithoutAKeyThatHasAValueIsEmpty(string json, bool expected)
    {
        // Act
        var isEmpty = Parse(json).IsEmpty;

        // Assert
        Assert.Equal(expected, isEmpty);
    }

    [Fact]
    public void WhenValuesAreBlankOrOfAnotherTypeOrAStringCannotBeRead_ThenTheyAreNotSet()
    {
        // Arrange
        var document = Parse("""{ "title": " ", "navTitle": 5, "icon": "\ud800", "location": 1, "alignment": "Up", "order": ["a", null, 2] }""");

        // Act
        var folder = document.FolderMetadata;
        var subject = document.SubjectMetadata;
        var order = document.ChildOrder;

        // Assert
        Assert.Null(folder);
        Assert.Null(subject);
        Assert.Equal(["a"], order.ToArray());
    }

    [Fact]
    public void WhenValuesAreMerged_ThenGivenKeysAreSetNullKeysAreRemovedAndOtherKeysStay()
    {
        // Arrange
        var document = Parse("""{ "title": "A", "icon": "Devices", "tags": ["x"], "extra": { "a": 1 } }""");

        // Act
        document.Merge(Values("""{ "title": "B", "icon": null, "extra": { "b": 2 } }"""), StorageMetadataKind.Folder);

        // Assert
        Assert.Equal("{\n  \"title\": \"B\",\n  \"tags\": [\n    \"x\"\n  ],\n  \"extra\": {\n    \"b\": 2\n  }\n}\n", Write(document));
    }

    [Fact]
    public void WhenAMergedKeyDiffersOnlyInCase_ThenItReplacesTheValueOfTheKeyOfTheFile()
    {
        // Arrange
        var document = Parse("""{ "NavTitle": "A", "Tags": 1 }""");

        // Act
        document.Merge(Values("""{ "navtitle": "B", "tags": 2 }"""), StorageMetadataKind.Subject);

        // Assert
        Assert.Equal("{\n  \"NavTitle\": \"B\",\n  \"Tags\": 2\n}\n", Write(document));
    }

    [Theory]
    [InlineData("""{ "location": "appbar", "alignment": "RIGHT" }""", "{\n  \"navTitle\": \"Pump\",\n  \"location\": \"AppBar\",\n  \"alignment\": \"Right\"\n}\n")]
    [InlineData("""{ "navTitle": "  " }""", "{}\n")]
    public void WhenKnownSubjectKeysAreMerged_ThenEnumNamesAreStoredAndABlankStringRemovesTheKey(string values, string expected)
    {
        // Arrange
        var document = Parse("""{ "navTitle": "Pump" }""");

        // Act
        document.Merge(Values(values), StorageMetadataKind.Subject);

        // Assert
        Assert.Equal(expected, Write(document));
    }

    [Theory]
    [InlineData("""[]""", "JSON object")]
    [InlineData("""{ "order": ["a"] }""", "'order'")]
    [InlineData("""{ "Order": null }""", "'Order'")]
    [InlineData("""{ "tags": 1, "Tags": 2 }""", "'Tags'")]
    [InlineData("""{ "title": 5 }""", "'title'")]
    [InlineData("""{ "navTitle": ["a"] }""", "'navTitle'")]
    [InlineData("""{ "note": "\ud800" }""", "'note'")]
    [InlineData("""{ "note": { "text": "\ud800" } }""", "'note'")]
    [InlineData("""{ "title": "\ud800" }""", "'title'")]
    public void WhenMergedValuesAreRefused_ThenTheMessageNamesTheKey(string values, string expectedPart)
    {
        // Arrange
        var document = Parse("{}");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => document.Merge(Values(values), StorageMetadataKind.Folder));
        Assert.Contains(expectedPart, exception.Message);
        Assert.Equal("values", exception.ParamName);
    }

    [Fact]
    public void WhenAnEnumValueIsUnknown_ThenItIsRefusedListingTheNames()
    {
        // Arrange
        var document = Parse("{}");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => document.Merge(Values("""{ "location": "Top" }"""), StorageMetadataKind.Subject));
        Assert.Contains("'location'", exception.Message);
        Assert.Contains("NavBar, AppBar", exception.Message);
    }

    [Theory]
    [MemberData(nameof(ValuesOverALimit))]
    public void WhenAMergeIsOverALimit_ThenItIsRefusedNamingTheKey(string json, string values, string expectedPart)
    {
        // Arrange
        var document = Parse(json);

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => document.Merge(Values(values), StorageMetadataKind.Folder));
        Assert.Contains(expectedPart, exception.Message);
    }

    [Theory]
    [MemberData(nameof(ValuesWithAControlCharacterInAKey))]
    public void WhenARefusedKeyHasControlCharacters_ThenTheMessageShowsThemEscaped(string values, string expectedPart)
    {
        // Arrange
        var document = Parse("{}");

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => document.Merge(Values(values), StorageMetadataKind.Folder));
        Assert.Contains(expectedPart, exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
    }

    [Fact]
    public void WhenNamesWithControlCharactersAreFormatted_ThenTheCharactersAreEscaped()
    {
        // Act
        var formatted = StorageMetadataDocument.FormatNames(["a\r\nb", "c"]);

        // Assert
        Assert.Equal("'a\\u000D\\u000Ab', 'c'", formatted);
    }

    [Theory]
    [InlineData("""{ "title": null }""")]
    [InlineData("""{ "title": "New" }""")]
    public void WhenTheFileIsOverTheSizeLimitAndAMergeDoesNotGrowIt_ThenTheMergeIsAccepted(string values)
    {
        // Arrange
        var document = Parse("""{ "title": "Old" }""");
        document.SetOrder(Enumerable.Range(0, 100).Select(index => $"{index}{new string('a', 200)}"));

        // Act
        document.Merge(Values(values), StorageMetadataKind.Folder);

        // Assert
        Assert.True(document.Serialize().Length > 16 * 1024);
        Assert.Equal(Values(values).GetProperty("title").GetString(), document.FolderMetadata?.Title);
    }

    [Fact]
    public void WhenTheFileIsOverTheSizeLimitAndAMergeGrowsIt_ThenTheMergeIsRefused()
    {
        // Arrange
        var document = Parse("""{ "title": "Old" }""");
        document.SetOrder(Enumerable.Range(0, 100).Select(index => $"{index}{new string('a', 200)}"));

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => document.Merge(Values("""{ "title": "Older" }"""), StorageMetadataKind.Folder));
        Assert.Contains("16 KB", exception.Message);
    }

    [Fact]
    public void WhenOrderIsMergedIntoTheMetadataOfASubject_ThenItIsStoredAsAnUnknownKey()
    {
        // Arrange
        var document = Parse("{}");

        // Act
        document.Merge(Values("""{ "order": ["a"] }"""), StorageMetadataKind.Subject);

        // Assert
        Assert.Equal("{\n  \"order\": [\n    \"a\"\n  ]\n}\n", Write(document));
    }

    [Fact]
    public void WhenOrderIsSet_ThenTheNamesAreStoredAndAnEmptyOrderRemovesTheKey()
    {
        // Arrange
        var document = Parse("""{ "title": "Docs" }""");

        // Act
        document.SetOrder(["b.md", "a.md"]);
        var withOrder = Write(document);
        document.SetOrder([]);

        // Assert
        Assert.Equal("{\n  \"title\": \"Docs\",\n  \"order\": [\n    \"b.md\",\n    \"a.md\"\n  ]\n}\n", withOrder);
        Assert.Equal("{\n  \"title\": \"Docs\"\n}\n", Write(document));
    }
}
