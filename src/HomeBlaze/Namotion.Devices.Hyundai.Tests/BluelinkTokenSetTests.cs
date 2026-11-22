using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkTokenSetTests
{
    private static readonly BluelinkTokenSet Tokens = new("access", "refresh", "exchangeable", "exchangeable-refresh", "non-ccs", "non-ccs-refresh", "id");

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
        Assert.DoesNotContain("refresh", text, StringComparison.Ordinal);
        Assert.DoesNotContain("access", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTextIsNotATokenSet_ThenParseThrowsFormatException()
    {
        // Act & Assert
        Assert.Throws<FormatException>(() => BluelinkTokenSet.Parse("not json"));
    }
}
