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
}
