using System.Text.Json;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkTokenSetTests
{
    private static readonly BluelinkTokenSet Tokens = new(
        "secret-access-1", "secret-refresh-1", "secret-exchangeable-1", "secret-exchangeable-refresh-1", "secret-non-ccs-1", "secret-non-ccs-refresh-1", "secret-id-1");

    [Fact]
    public void WhenTokenSetIsSerialized_ThenItParsesBack()
    {
        // Act
        var parsed = BluelinkTokenSet.Parse(Tokens.Serialize());

        // Assert
        Assert.Equal(Tokens, parsed);
    }

    [Fact]
    public void WhenTokenSetIsPrinted_ThenNoTokenIsShown()
    {
        // Act
        var text = Tokens.ToString();

        // Assert
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenCcsTokenIsPrinted_ThenTheAccessTokenIsLeftOut()
    {
        // Arrange
        var token = new BluelinkCcsToken("secret-ccs-1", DateTimeOffset.FromUnixTimeSeconds(1700000000));

        // Act
        var text = token.ToString();

        // Assert
        Assert.DoesNotContain("secret", text, StringComparison.Ordinal);
        Assert.Contains("2023-11-14", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenResponseLacksTokens_ThenPreviousValuesAreKept()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"accessToken":"new-access","refreshToken":"new-refresh","idToken":""}""");

        // Act
        var tokens = BluelinkTokenSet.FromJson(document.RootElement, Tokens);

        // Assert
        Assert.Equal(
            Tokens with { AccessToken = "new-access", RefreshToken = "new-refresh" },
            tokens);
    }

    [Fact]
    public void WhenNeitherResponseNorPreviousSetHasRefreshToken_ThenFromJsonReturnsNull()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"accessToken":"new-access","refreshToken":""}""");

        // Act
        var tokens = BluelinkTokenSet.FromJson(document.RootElement, previous: null);

        // Assert
        Assert.Null(tokens);
    }

    [Fact]
    public void WhenResponseLacksRefreshToken_ThenThePreviousRefreshTokenIsKept()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"accessToken":"new-access"}""");

        // Act
        var tokens = BluelinkTokenSet.FromJson(document.RootElement, Tokens);

        // Assert
        Assert.Equal(Tokens with { AccessToken = "new-access" }, tokens);
    }

    [Fact]
    public void WhenTextIsNotATokenSet_ThenParseThrowsFormatException()
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => BluelinkTokenSet.Parse("not json"));
    }
}
