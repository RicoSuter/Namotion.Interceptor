using System.Text;
using HomeBlaze.Components.Abstractions.Pages;
using HomeBlaze.Services;
using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class StorageMetadataFileTests
{
    private static StorageMetadataFile Parse(string json)
        => StorageMetadataFile.TryParse(Encoding.UTF8.GetBytes(json), out var file, out var error) ? file : throw error;

    [Fact]
    public void WhenTheOrderIsReadTwice_ThenItIsReadFromTheFileOnce()
    {
        // Arrange
        var file = Parse("""{ "order": ["b.md", 1, "a.md"] }""");

        // Act
        var first = file.Order;
        var second = file.Order;

        // Assert
        Assert.Equal(["b.md", "a.md"], first.ToArray());
        Assert.True(first == second);
        Assert.Empty(Parse("""{ "title": "Docs" }""").Order);
        Assert.Empty(StorageMetadataFile.Empty.Order);
    }

    [Fact]
    public void WhenAFileIsParsed_ThenItsOwnKeysAndItsEntriesAreRead()
    {
        // Arrange
        const string json = """
            {
              // A comment and a trailing comma are allowed.
              "navTitle": "Shelly Pro 4PM",
              "icon": "ElectricalServices",
              "note": null,
              "$subjects": {
                "Channel": { "navTitle": "Pump relay" },
                "Channels[0]": { "navTitle": "Hall light" },
                "Meters['Main']/Phases[2]": { "area": "Utility room" }
              },
              "$properties": {
                "Firmware": { "comment": "auto-update disabled" },
                "Channels/0/Power": { "comment": "includes the hall heater" }
              },
            }
            """;

        // Act
        var file = Parse(json);

        // Assert
        Assert.Equal("Shelly Pro 4PM", file.Own.GetString(MetadataKeys.NavigationTitle));
        Assert.Equal(2, file.Own!.Count);
        Assert.Equal("Pump relay", file.FindSubject("Channel").GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("Hall light", file.FindSubject("Channels/0").GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("Utility room", file.FindSubject("Meters/Main/Phases/2").GetString("area"));
        Assert.Equal("auto-update disabled", file.FindProperty("Firmware").GetString("comment"));
        Assert.Equal("includes the hall heater", file.FindProperty("Channels/0/Power").GetString("comment"));
        Assert.Null(file.FindSubject("Firmware"));
        Assert.Empty(file.DuplicatePaths);
    }

    [Fact]
    public void WhenAFileStartsWithAByteOrderMark_ThenItIsParsed()
    {
        // Arrange
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{ "title": "Docs" }""")).ToArray();

        // Act
        var isParsed = StorageMetadataFile.TryParse(content, out var file, out _);

        // Assert
        Assert.True(isParsed);
        Assert.Equal("Docs", file!.Own.GetString(MetadataKeys.Title));
    }

    [Fact]
    public void WhenTwoEntriesHaveTheSameTarget_ThenTheFirstAppliesAndTheOtherIsReported()
    {
        // Arrange
        const string json = """{ "$subjects": { "Channels[0]": { "navTitle": "First" }, "Channels/0": { "navTitle": "Second" } } }""";

        // Act
        var file = Parse(json);

        // Assert
        Assert.Equal("First", file.FindSubject("Channels/0").GetString(MetadataKeys.NavigationTitle));
        Assert.Equal(["Channels/0"], file.DuplicatePaths);
    }

    [Fact]
    public void WhenTheFirstEntryOfATargetHasNoKeys_ThenTheOtherIsReportedAndDoesNotApply()
    {
        // Arrange
        const string json = """{ "$subjects": { "Channels[0]": { "navTitle": null }, "Channels/0": { "navTitle": "Second" } } }""";

        // Act
        var file = Parse(json);

        // Assert
        Assert.Null(file.FindSubject("Channels/0"));
        Assert.Equal(["Channels/0"], file.DuplicatePaths);
    }

    [Fact]
    public void WhenAFileHasReservedKeys_ThenTheyAreNoMetadataAndATypeIsReported()
    {
        // Act
        var file = Parse("""{ "$type": "HomeBlaze.Samples.Motor", "$comment": "x", "title": "Docs" }""");

        // Assert
        Assert.True(file.HoldsSubjectType);
        Assert.Equal(["title"], file.Own!.Keys.ToArray());
    }

    [Fact]
    public void WhenKeysDifferOnlyInCase_ThenTheyAreDifferentKeysAndOnlyTheExactOnesAreKnown()
    {
        // Act
        var file = Parse("""{ "navTitle": "Pump", "NavTitle": "Other", "$Subjects": { "Channel": { "navTitle": "Relay" } } }""");

        // Assert
        Assert.Equal("Pump", file.Own.GetString(MetadataKeys.NavigationTitle));
        Assert.Equal("Other", file.Own.GetString("NavTitle"));
        Assert.Equal(2, file.Own!.Count);
        Assert.False(file.HasSubjects);
    }

    [Fact]
    public void WhenAnEntryHasNoKeys_ThenItIsLeftOut()
    {
        // Act
        var file = Parse("""{ "$subjects": { "Channel": {}, "Meter": { "area": null } } }""");

        // Assert
        Assert.Null(file.Own);
        Assert.False(file.HasSubjects);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("{ \"a\": 1, \"a\": 2 }")]
    [InlineData("{ \"\\ud800\": 1 }")]
    [InlineData("{ \"$subjects\": [] }")]
    [InlineData("{ \"$subjects\": { \"Channel\": 1 } }")]
    [InlineData("{ \"$properties\": { \"Channels[0\": { \"a\": 1 } } }")]
    [InlineData("{ \"$properties\": { \"Power\": { \"a\": 1, \"a\": 2 } } }")]
    public void WhenContentCannotBeRead_ThenItIsNoMetadata(string json)
    {
        // Act
        var isParsed = StorageMetadataFile.TryParse(Encoding.UTF8.GetBytes(json), out var file, out var error);

        // Assert
        Assert.False(isParsed);
        Assert.Null(file);
        Assert.NotNull(error);
    }
}
