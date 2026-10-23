using System.Runtime.InteropServices;

namespace HomeBlaze.App.Tests;

public class ExternalLinksTests
{
    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://example.com:8080/path?query=1#fragment")]
    [InlineData("HTTPS://Example.com/")]
    [InlineData("mailto:someone@example.com?subject=Hello")]
    public void WhenTheUrlIsAbsoluteWebOrMail_ThenItIsAccepted(string candidate)
    {
        // Act
        var accepted = ExternalLinks.TryCreate(candidate, out var uri);

        // Assert
        Assert.True(accepted);
        Assert.NotNull(uri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/relative/path")]
    [InlineData("example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ftp://example.com/")]
    [InlineData("ms-settings:privacy")]
    [InlineData("vscode://file/tmp/x")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("\\\\server\\share\\file.exe")]
    public void WhenTheUrlIsNotAbsoluteWebOrMail_ThenItIsRejected(string? candidate)
    {
        // Act
        var accepted = ExternalLinks.TryCreate(candidate, out var uri);

        // Assert
        Assert.False(accepted);
        Assert.Null(uri);
    }

    [Fact]
    public void WhenTheMessageCarriesAValidUrl_ThenItIsParsed()
    {
        // Act
        var parsed = ExternalLinks.TryParseMessage("shell:open-external:https://example.com/docs", out var uri);

        // Assert
        Assert.True(parsed);
        Assert.Equal("https://example.com/docs", uri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("shell:settings")]
    [InlineData("shell:open-external:")]
    [InlineData("shell:open-external:file:///etc/passwd")]
    [InlineData("SHELL:OPEN-EXTERNAL:https://example.com/")]
    [InlineData("https://example.com/")]
    [InlineData("{\"type\":\"open\",\"id\":\"a\"}")]
    public void WhenTheMessageIsNotAnOpenExternalMessageWithAValidUrl_ThenItIsNotParsed(string message)
    {
        // Act
        var parsed = ExternalLinks.TryParseMessage(message, out var uri);

        // Assert
        Assert.False(parsed);
        Assert.Null(uri);
    }

    [Fact]
    public void WhenTheUrlContainsSpacesOrQuotes_ThenTheOpenerReceivesItEscaped()
    {
        // Arrange
        ExternalLinks.TryCreate("https://example.com/a b\"c --flag", out var uri);

        // Act
        var startInfo = ExternalLinks.CreateStartInfo(uri!, OSPlatform.Linux);

        // Assert
        var argument = Assert.Single(startInfo.ArgumentList);
        Assert.Equal("https://example.com/a%20b%22c%20--flag", argument);
    }

    [Theory]
    [InlineData("OSX", "open")]
    [InlineData("Linux", "xdg-open")]
    [InlineData("FreeBSD", "xdg-open")]
    public void WhenOpeningOnUnix_ThenTheOpenerGetsTheUrlAsItsOnlyArgument(string platformName, string expectedOpener)
    {
        // Arrange
        ExternalLinks.TryCreate("https://example.com/", out var uri);

        // Act
        var startInfo = ExternalLinks.CreateStartInfo(uri!, OSPlatform.Create(platformName));

        // Assert
        Assert.Equal(expectedOpener, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Equal(["https://example.com/"], startInfo.ArgumentList);
    }

    [Fact]
    public void WhenOpeningOnWindows_ThenTheUrlIsShellExecutedWithoutArguments()
    {
        // Arrange
        ExternalLinks.TryCreate("https://example.com/", out var uri);

        // Act
        var startInfo = ExternalLinks.CreateStartInfo(uri!, OSPlatform.Windows);

        // Assert
        Assert.Equal("https://example.com/", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Empty(startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
    }
}
