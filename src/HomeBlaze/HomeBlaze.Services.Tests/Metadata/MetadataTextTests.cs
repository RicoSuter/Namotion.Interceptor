using HomeBlaze.Services.Metadata;

namespace HomeBlaze.Services.Tests.Metadata;

public class MetadataTextTests
{
    [Theory]
    [InlineData("Pump", false)]
    [InlineData("Pump \U0001F600", false)]
    [InlineData("a\nb", true)]
    [InlineData("a‮b", true)]
    [InlineData("a​b", true)]
    [InlineData("Pump\U000E0041\U000E0042", true)]
    public void WhenATextIsChecked_ThenControlAndFormatCharactersAreFoundAlsoOutsideTheBasicPlane(string text, bool expected)
    {
        // Act
        var hasControlOrFormatCharacter = MetadataText.HasControlOrFormatCharacter(text);

        // Assert
        Assert.Equal(expected, hasControlOrFormatCharacter);
    }
}
