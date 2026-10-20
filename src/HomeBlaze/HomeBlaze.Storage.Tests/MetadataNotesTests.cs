namespace HomeBlaze.Storage.Tests;

public class MetadataNotesTests
{
    [Fact]
    public void WhenNotesFollowTheRules_ThenTheyAreRenderedAsHtml()
    {
        // Act
        var html = MetadataNotes.ToHtml("Calibrated **2026**, see [Setup](/pages/Docs/Setup.md).");

        // Assert
        Assert.Equal("<p>Calibrated <strong>2026</strong>, see <a href=\"/pages/Docs/Setup.md\">Setup</a>.</p>\n", html);
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
    [InlineData("[Setup](/pages/Docs/Setup.md){.mud-button}")]
    [InlineData("[Setup](/Docs/Setup.md)")]
    [InlineData("[Settings](/settings)")]
    [InlineData("Text {data-value=1}")]
    [InlineData("::: Card\n:::")]
    [InlineData("::: {.mud-overlay}\nText\n:::")]
    [InlineData("$$\nx^2\n$$")]
    [InlineData("*[HTML]: Hyper Text\n\nHTML")]
    [InlineData("Calibrated[^1]\n\n[^1]: Setup.md")]
    public void WhenNotesBreakTheRules_ThenTheyAreNotRenderedAsHtml(string notes)
    {
        // Act
        var html = MetadataNotes.ToHtml(notes);

        // Assert
        Assert.Null(html);
        Assert.NotNull(MetadataNotes.GetError(notes));
    }

    [Theory]
    [InlineData("<b>Pump</b>", "HTML")]
    [InlineData("![pump](https://example.com/pump.png)", "pictures")]
    [InlineData("{{ Temperature }}", "{{ }} expressions")]
    [InlineData("[Setup](/Docs/Setup.md)", "'/Docs/Setup.md'")]
    [InlineData("Text {.mud-overlay}", "attributes")]
    public void WhenNotesBreakARule_ThenTheErrorNamesTheRule(string notes, string expectedRule)
    {
        // Act
        var error = MetadataNotes.GetError(notes);

        // Assert
        Assert.Contains(expectedRule, error);
    }

    [Fact]
    public void WhenNotesHaveAFootnote_ThenItIsRenderedAsText()
    {
        // Act
        var html = MetadataNotes.ToHtml("Calibrated[^1]\n\n[^1]: In 2026.");

        // Assert
        Assert.Equal("<p>Calibrated[^1]</p>\n<p>[^1]: In 2026.</p>\n", html);
    }

    [Theory]
    [InlineData("Range {low}", "<p>Range {low}</p>\n")]
    [InlineData("Range {low high}", "<p>Range {low high}</p>\n")]
    public void WhenNotesHaveBracesThatGiveNoIdentifierClassOrValue_ThenTheyAreRenderedAsText(string notes, string expectedHtml)
    {
        // Act
        var html = MetadataNotes.ToHtml(notes);

        // Assert
        Assert.Equal(expectedHtml, html);
    }

    [Theory]
    [InlineData("[Home](/)")]
    [InlineData("[Browser](/browser)")]
    [InlineData("[Pump](/browser/Devices/Pump)")]
    [InlineData("[Setup](/pages/Docs/Setup.md)")]
    public void WhenNotesLinkToARouteOfTheApplication_ThenTheyAreAllowed(string notes)
    {
        // Act
        var error = MetadataNotes.GetError(notes);

        // Assert
        Assert.Null(error);
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
