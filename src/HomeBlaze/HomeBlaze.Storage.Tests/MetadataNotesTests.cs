namespace HomeBlaze.Storage.Tests;

public class MetadataNotesTests
{
    [Fact]
    public void WhenNotesFollowTheRules_ThenTheyAreRenderedAsHtml()
    {
        // Act
        var html = MetadataNotes.ToHtml("Calibrated **2026**, see [Setup](/Docs/Setup.md).");

        // Assert
        Assert.Equal("<p>Calibrated <strong>2026</strong>, see <a href=\"/Docs/Setup.md\">Setup</a>.</p>\n", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("[run](javascript:alert(1))")]
    [InlineData("![pump](https://example.com/pump.png)")]
    [InlineData("{{ Temperature }}")]
    [InlineData("[home](//example.com)")]
    [InlineData(@"[home](/\example.com)")]
    [InlineData(@"[home](\\\\example.com)")]
    [InlineData("[home]\n\n[home]: //example.com")]
    [InlineData("[Setup](Setup.md)")]
    [InlineData("[Setup](../Docs/Setup.md)")]
    [InlineData("[Calibration](#calibration)")]
    [InlineData("[Setup]\n\n[Setup]: Setup.md")]
    [InlineData("Text {.mud-overlay}")]
    [InlineData("# Pump {#app}")]
    [InlineData("[Setup](/Docs/Setup.md){.mud-button}")]
    [InlineData("Text {data-value=1}")]
    [InlineData("::: Card\n:::")]
    public void WhenNotesBreakTheRules_ThenTheyAreNotRenderedAsHtml(string notes)
    {
        // Act
        var html = MetadataNotes.ToHtml(notes);

        // Assert
        Assert.Null(html);
        Assert.NotNull(MetadataNotes.GetError(notes));
    }

    [Fact]
    public void WhenNotesAreLargerThanTheLimit_ThenTheyAreRefused()
    {
        // Act
        var error = MetadataNotes.GetError(new string('a', MetadataNotes.MaxSize + 1));

        // Assert
        Assert.Contains("4 KB", error);
    }

    [Fact]
    public void WhenNotesHaveAHeading_ThenItIsRenderedWithoutIdentifier()
    {
        // Act
        var html = MetadataNotes.ToHtml("# Pump\n\n- [x] Calibrated");

        // Assert
        Assert.StartsWith("<h1>Pump</h1>\n", html);
    }

    [Theory]
    [InlineData("[Manual](https://example.com/manual.pdf)", "<a href=\"https://example.com/manual.pdf\" target=\"_blank\" rel=\"noopener noreferrer\">Manual</a>")]
    [InlineData("<http://example.com>", "<a href=\"http://example.com\" target=\"_blank\" rel=\"noopener noreferrer\">http://example.com</a>")]
    [InlineData("[Mail](mailto:service@example.com)", "<a href=\"mailto:service@example.com\">Mail</a>")]
    [InlineData("[Pump](/browser/Devices/Pump)", "<a href=\"/browser/Devices/Pump\">Pump</a>")]
    public void WhenNotesHaveALink_ThenAWebAddressOpensInANewTabWithoutReferrer(string notes, string expectedLink)
    {
        // Act
        var html = MetadataNotes.ToHtml(notes);

        // Assert
        Assert.Contains(expectedLink, html);
    }

    [Theory]
    [InlineData("\u20AC", 2000, 1365)]
    [InlineData("\U0001F600", 2000, 2048)]
    [InlineData("a", 100, 100)]
    public void WhenNotesAreShownAsText_ThenTheyAreCutToTheLimitAtAWholeCharacter(string character, int count, int expectedLength)
    {
        // Arrange
        var notes = string.Concat(Enumerable.Repeat(character, count));

        // Act
        var text = MetadataNotes.ToText(notes);

        // Assert
        Assert.Equal(expectedLength, text.Length);
        Assert.Equal(notes[..expectedLength], text);
    }
}
