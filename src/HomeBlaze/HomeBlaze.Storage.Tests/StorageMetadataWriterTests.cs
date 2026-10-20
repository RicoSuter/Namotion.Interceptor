using System.Text;
using System.Text.Json;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataWriterTests
{
    private static readonly StorageMetadataTarget Folder = new(".metadata.json", null, null, IsContainer: true);

    private static readonly StorageMetadataTarget Subject = new("Shelly.metadata.json", null, null, IsContainer: false);

    private static StorageMetadataTarget NestedSubject(string path) => new("Shelly.metadata.json", StorageMetadataFile.SubjectsKey, path, IsContainer: false);

    private static StorageMetadataTarget Property(string path) => new("Shelly.metadata.json", StorageMetadataFile.PropertiesKey, path, IsContainer: false);

    private static string? Merge(string? file, StorageMetadataTarget target, string values)
    {
        var written = StorageMetadataWriter.Merge(file is null ? null : Encoding.UTF8.GetBytes(file), target, JsonElement.Parse(values));
        return written is { } result ? Encoding.UTF8.GetString(result.Content) : null;
    }

    private static string JsonObject(IEnumerable<string> members) => "{" + string.Join(", ", members) + "}";

    // An array of strings of 200 characters, which as a JSON value takes about 203 bytes each.
    private static string LongStrings(int count) => "[" + string.Join(", ", Enumerable.Repeat($"\"{new string('a', 200)}\"", count)) + "]";

    // The file, the values merged into it, and a part of the message.
    public static TheoryData<string, string, string> ValuesOverALimit() => new()
    {
        { "{}", $$"""{ "{{new string('k', 65)}}": 1 }""", "64 characters" },
        { "{}", $$"""{ "note": "{{new string('a', 257)}}" }""", "'note'" },
        { "{}", $$"""{ "notes": {{LongStrings(21)}} }""", "'notes'" },
        { "{}", JsonObject(Enumerable.Range(0, 65).Select(index => $"\"key{index}\": null")), "64 keys" },
        { JsonObject(Enumerable.Range(0, 64).Select(index => $"\"key{index}\": {index}")), """{ "extra": 1 }""", "'extra'" }
    };

    public static TheoryData<string, string> ValuesWithAControlCharacterInAKey() => new()
    {
        { """{ "a\r\nb": 1, "a\r\nb": 2 }""", @"'a\u000D\u000Ab'" },
        { $$"""{ "a\r\nb": "{{new string('x', 257)}}" }""", @"'a\u000D\u000Ab'" }
    };

    [Fact]
    public void WhenValuesAreMerged_ThenGivenKeysAreSetNullKeysAreRemovedAndOtherKeysStay()
    {
        // Act
        var written = Merge("""{ "title": "A", "icon": "Devices", "groups": ["x"], "extra": { "a": 1 } }""", Folder,
            """{ "title": "B", "icon": null, "extra": { "b": 2 } }""");

        // Assert
        Assert.Equal("{\n  \"title\": \"B\",\n  \"groups\": [\n    \"x\"\n  ],\n  \"extra\": {\n    \"b\": 2\n  }\n}\n", written);
    }

    [Fact]
    public void WhenAFileIsWritten_ThenItIsIndentedKeepsNonAsciiAndLeavesOutNullsAndComments()
    {
        // Act
        var written = Merge("""{ /* note */ "title": "Geräte", "note": null }""", Folder, """{ "icon": "Küche" }""");

        // Assert
        Assert.Equal("{\n  \"title\": \"Geräte\",\n  \"icon\": \"Küche\"\n}\n", written);
    }

    [Fact]
    public void WhenAValueHoldsCharactersOutsideTheBasicPlane_ThenTheyAreWrittenAsEscapes()
    {
        // Act
        var written = Merge("""{ "title": "Pump 😀" }""", Folder, """{ "note": "say \"hi\" \\ 🌱\t" }""");

        // Assert
        Assert.Equal("{\n  \"title\": \"Pump \\uD83D\\uDE00\",\n  \"note\": \"say \\\"hi\\\" \\\\ \\uD83C\\uDF31\\t\"\n}\n", written);
    }

    [Fact]
    public void WhenAMergedKeyDiffersOnlyInCase_ThenItIsAnotherKey()
    {
        // Act
        var written = Merge("""{ "NavTitle": "A", "Groups": 1 }""", Subject, """{ "navTitle": "B", "groups": 2 }""");

        // Assert
        Assert.Equal("{\n  \"NavTitle\": \"A\",\n  \"Groups\": 1,\n  \"navTitle\": \"B\",\n  \"groups\": 2\n}\n", written);
    }

    [Fact]
    public void WhenAKnownKeyInAnotherCaseIsRemoved_ThenOnlyThatKeyIsRemoved()
    {
        // Act
        var written = Merge("""{ "NavTitle": "A", "navTitle": "B" }""", Subject, """{ "NavTitle": null }""");

        // Assert
        Assert.Equal("{\n  \"navTitle\": \"B\"\n}\n", written);
    }

    [Theory]
    [InlineData("""{ "location": "appbar", "alignment": "RIGHT" }""", "{\n  \"navTitle\": \"Pump\",\n  \"location\": \"AppBar\",\n  \"alignment\": \"Right\"\n}\n")]
    [InlineData("""{ "navTitle": "  " }""", null)]
    public void WhenKnownKeysAreMerged_ThenEnumNamesAreStoredAndABlankStringRemovesTheKey(string values, string? expected)
    {
        // Act
        var written = Merge("""{ "navTitle": "Pump" }""", Subject, values);

        // Assert
        Assert.Equal(expected, written);
    }

    [Fact]
    public void WhenTheEntryOfANestedSubjectIsWritten_ThenItIsAddedUnderItsPath()
    {
        // Act
        var written = Merge("""{ "navTitle": "Shelly" }""", NestedSubject("Channels[0]"), """{ "navTitle": "Hall light" }""");

        // Assert
        Assert.Equal("{\n  \"navTitle\": \"Shelly\",\n  \"$subjects\": {\n    \"Channels[0]\": {\n      \"navTitle\": \"Hall light\"\n    }\n  }\n}\n", written);
    }

    [Fact]
    public void WhenAnEntryWasReadInTheRouteNotation_ThenItIsMergedAndWrittenWithThePathOfTheTarget()
    {
        // Act
        var written = Merge("""{ "$properties": { "Channels/0/Power": { "comment": "old", "unit": "W" } } }""",
            Property("Channels[0]/Power"), """{ "comment": "includes the hall heater" }""");

        // Assert
        Assert.Equal("{\n  \"$properties\": {\n    \"Channels[0]/Power\": {\n      \"comment\": \"includes the hall heater\",\n      \"unit\": \"W\"\n    }\n  }\n}\n", written);
    }

    [Fact]
    public void WhenPathsDifferOnlyInCase_ThenTheyAreEntriesOfDifferentTargets()
    {
        // Act
        var written = Merge("""{ "$properties": { "Power": { "comment": "a" } } }""", Property("power"), """{ "comment": "b" }""");

        // Assert
        Assert.Equal("{\n  \"$properties\": {\n    \"Power\": {\n      \"comment\": \"a\"\n    },\n    \"power\": {\n      \"comment\": \"b\"\n    }\n  }\n}\n", written);
    }

    [Fact]
    public void WhenTheFileHasPathsThatDifferOnlyInCase_ThenAnotherWriteSucceeds()
    {
        // Arrange
        const string file = """{ "$properties": { "Power": { "comment": "a" }, "power": { "comment": "b" } } }""";

        // Act
        var written = Merge(file, Property("Power"), """{ "comment": "c" }""");

        // Assert
        Assert.Equal("{\n  \"$properties\": {\n    \"Power\": {\n      \"comment\": \"c\"\n    },\n    \"power\": {\n      \"comment\": \"b\"\n    }\n  }\n}\n", written);
    }

    [Fact]
    public void WhenAnEntryInTheRouteNotationIsWritten_ThenItKeepsItsPlaceInTheSection()
    {
        // Arrange
        const string file = """{ "$subjects": { "Channels/0": { "a": 1 }, "Channel": { "b": 2 } } }""";

        // Act
        var written = Merge(file, NestedSubject("Channels[0]"), """{ "a": 3 }""");

        // Assert
        Assert.Equal("{\n  \"$subjects\": {\n    \"Channels[0]\": {\n      \"a\": 3\n    },\n    \"Channel\": {\n      \"b\": 2\n    }\n  }\n}\n", written);
    }

    [Fact]
    public void WhenTheFileHasTwoEntriesOfTheTarget_ThenTheWriteIsRefused()
    {
        // Arrange
        const string file = """{ "$subjects": { "Channels[0]": { "a": 1 }, "Channels/0": { "b": 2 } } }""";

        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge(file, NestedSubject("Channels[0]"), """{ "c": 3 }"""));
        Assert.Contains("'Channels[0]', 'Channels/0'", exception.Message);
    }

    [Theory]
    [InlineData("Holder[0")]
    [InlineData("Holder[0/Power")]
    public void WhenThePathOfTheTargetCannotBeReadBack_ThenTheWriteIsRefusedNamingIt(string path)
    {
        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge(null, NestedSubject(path), """{ "navTitle": "Pump" }"""));
        Assert.Contains($"'{path}'", exception.Message);
    }

    [Fact]
    public void WhenTheLastKeyOfAnEntryIsRemoved_ThenTheEntryAndItsEmptySectionAreRemoved()
    {
        // Act
        var written = Merge("""{ "icon": "Water", "$subjects": { "Channel": { "navTitle": "Pump" } } }""", NestedSubject("Channel"), """{ "navTitle": null }""");

        // Assert
        Assert.Equal("{\n  \"icon\": \"Water\"\n}\n", written);
    }

    [Theory]
    [InlineData("""{ "title": null }""")]
    [InlineData("""{ "title": "  " }""")]
    public void WhenTheFileHoldsNoKeyAfterAWrite_ThenItIsDeleted(string values)
    {
        // Act
        var written = Merge("""{ "title": "Docs" }""", Folder, values);

        // Assert
        Assert.Null(written);
    }

    [Fact]
    public void WhenOrderIsWrittenToAFolder_ThenItIsStoredAndAnEmptyOrderRemovesIt()
    {
        // Act
        var withOrder = Merge("""{ "title": "Docs" }""", Folder, """{ "order": ["b.md", "a.md"] }""");
        var withoutOrder = Merge(withOrder, Folder, """{ "order": [] }""");

        // Assert
        Assert.Equal("{\n  \"title\": \"Docs\",\n  \"order\": [\n    \"b.md\",\n    \"a.md\"\n  ]\n}\n", withOrder);
        Assert.Equal("{\n  \"title\": \"Docs\"\n}\n", withoutOrder);
    }

    [Fact]
    public void WhenOrderIsLongerThanAValueMayBe_ThenItIsStillWritten()
    {
        // Arrange
        var names = string.Join(", ", Enumerable.Range(0, 100).Select(index => $"\"{index}{new string('a', 200)}\""));

        // Act
        var written = Merge(null, Folder, $$"""{ "order": [{{names}}] }""");

        // Assert
        Assert.True(written!.Length > 16 * 1024);
    }

    [Theory]
    [InlineData("""[]""", "JSON object")]
    [InlineData("""{ "groups": 1, "groups": 2 }""", "'groups'")]
    [InlineData("""{ "NavTitle": "Pump" }""", "'navTitle'")]
    [InlineData("""{ "title": 5 }""", "'title'")]
    [InlineData("""{ "navTitle": ["a"] }""", "'navTitle'")]
    [InlineData("""{ "note": "\ud800" }""", "'note'")]
    [InlineData("""{ "note": "\t\ud800" }""", "'note'")]
    [InlineData("""{ "note": { "text": "\ud800" } }""", "'note'")]
    [InlineData("""{ "title": "\ud800" }""", "'title'")]
    [InlineData("""{ "\ud800": "x" }""", "lone surrogate")]
    [InlineData("""{ "$subjects": {} }""", "'$subjects'")]
    [InlineData("""{ "order": "a.md" }""", "'order'")]
    [InlineData("""{ "order": ["a.md", 1] }""", "'order'")]
    public void WhenMergedValuesAreRefused_ThenTheMessageNamesTheKey(string values, string expectedPart)
    {
        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge("{}", Folder, values));
        Assert.Contains(expectedPart, exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenOrderIsWrittenToAnythingButAFolder_ThenItIsRefused(bool isNested)
    {
        // Arrange
        var target = isNested ? NestedSubject("Channel") : Subject;

        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge("{}", target, """{ "order": ["a"] }"""));
        Assert.Contains("only a folder or the root", exception.Message);
    }

    [Fact]
    public void WhenAnEnumValueIsUnknown_ThenItIsRefusedListingTheNames()
    {
        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge("{}", Subject, """{ "location": "Top" }"""));
        Assert.Contains("'location'", exception.Message);
        Assert.Contains("NavBar, AppBar", exception.Message);
    }

    [Theory]
    [MemberData(nameof(ValuesOverALimit))]
    public void WhenAMergeIsOverALimit_ThenItIsRefusedNamingTheKey(string file, string values, string expectedPart)
    {
        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge(file, Folder, values));
        Assert.Contains(expectedPart, exception.Message);
    }

    [Theory]
    [MemberData(nameof(ValuesWithAControlCharacterInAKey))]
    public void WhenARefusedKeyHasControlCharacters_ThenTheMessageShowsThemEscaped(string values, string expectedPart)
    {
        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge("{}", Folder, values));
        Assert.Contains(expectedPart, exception.Message);
        Assert.DoesNotContain('\r', exception.Message);
        Assert.DoesNotContain('\n', exception.Message);
    }

    [Theory]
    [InlineData("""{ "title": null }""")]
    [InlineData("""{ "title": "New" }""")]
    public void WhenTheFileIsOverTheSizeLimitAndAMergeDoesNotGrowIt_ThenTheMergeIsAccepted(string values)
    {
        // Arrange
        var file = $$"""{ "title": "Old", "order": [{{LargeOrder()}}] }""";

        // Act
        var written = Merge(file, Folder, values);

        // Assert
        Assert.True(written!.Length > StorageMetadataWriter.MaxFileSize);
    }

    [Fact]
    public void WhenTheFileIsOverTheSizeLimitAndAMergeGrowsIt_ThenTheMergeIsRefused()
    {
        // Arrange
        var file = $$"""{ "title": "Old", "order": [{{LargeOrder()}}] }""";

        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge(file, Folder, """{ "title": "Older" }"""));
        Assert.Contains("512 KB", exception.Message);
    }

    [Fact]
    public void WhenTheFileHasAValueThatCannotBeWrittenBack_ThenTheMergeIsRefusedNamingTheFile()
    {
        // Act & Assert
        var exception = Assert.Throws<StorageRefusedException>(() => Merge("""{ "note": "\ud800" }""", Folder, """{ "title": "Docs" }"""));
        Assert.Contains("'.metadata.json' could not be read", exception.Message);
    }

    [Theory]
    [InlineData("""{ "$subjects": { "Channels[0]": { "navTitle": "A" } }, "$subjects": { "Channels[1]": { "navTitle": "B" } } }""", "Channels[0]")]
    [InlineData("""{ "$subjects": { "Channels[0]": { "navTitle": "A", "navTitle": "B" } } }""", "Channels[0]")]
    [InlineData("""{ "$subjects": { "Channels[0]": { "navTitle": "A", "navTitle": "B" } } }""", "Channels[1]")]
    [InlineData("""{ "$subjects": { "Channels[0]": 5 } }""", "Channels[0]")]
    [InlineData("""{ "$subjects": 5 }""", "Channels[0]")]
    public void WhenAPassCannotReadTheFile_ThenTheMergeIsRefusedNamingTheFile(string file, string path)
    {
        // Act
        var exception = Record.Exception(() => Merge(file, NestedSubject(path), """{ "navTitle": "C" }"""));
        var isRead = StorageMetadataFile.TryParse(Encoding.UTF8.GetBytes(file), out _, out _);

        // Assert
        var refusal = Assert.IsType<StorageRefusedException>(exception);
        Assert.Contains("'Shelly.metadata.json' could not be read", refusal.Message);
        Assert.False(isRead);
    }

    [Fact]
    public void WhenTheFileHasThePathOfAnEntryTwice_ThenTheMergeIsRefused()
    {
        // Arrange
        const string file = """{ "$subjects": { "Channels[0]": { "navTitle": "A" }, "Channels[0]": { "navTitle": "B" } } }""";

        // Act
        var exception = Record.Exception(() => Merge(file, NestedSubject("Channels[1]"), """{ "navTitle": "C" }"""));

        // Assert
        var refusal = Assert.IsType<StorageRefusedException>(exception);
        Assert.Contains("'Shelly.metadata.json' could not be read", refusal.Message);
    }

    [Fact]
    public void WhenValuesAreMerged_ThenTheFileIsReturnedAsAPassReadsIt()
    {
        // Act
        var written = StorageMetadataWriter.Merge(null, NestedSubject("Channels[0]"), JsonElement.Parse("""{ "navTitle": "Hall light" }"""));

        // Assert
        Assert.Equal("Hall light", written!.Value.File.FindSubject("Channels/0")!["navTitle"].GetString());
    }

    // Names of children that make a file of more than 512 KB.
    private static string LargeOrder()
        => string.Join(", ", Enumerable.Range(0, 2700).Select(index => $"\"{index}{new string('a', 200)}\""));
}
