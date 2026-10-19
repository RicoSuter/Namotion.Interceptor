using HomeBlaze.Storage.Internal;

namespace HomeBlaze.Storage.Tests;

public class NameListTests
{
    [Fact]
    public void WhenNamesWithControlCharactersAreFormatted_ThenTheCharactersAreEscaped()
    {
        // Act
        var formatted = NameList.Format(["a\r\nb", "c"]);

        // Assert
        Assert.Equal("'a\\u000D\\u000Ab', 'c'", formatted);
    }

    [Fact]
    public void WhenManyLongNamesAreFormatted_ThenTheFirstTenAreShownShortened()
    {
        // Arrange
        var names = Enumerable.Range(0, 20).Select(index => $"{index:D2}{new string('x', 100)}");

        // Act
        var formatted = NameList.Format(names);

        // Assert
        Assert.StartsWith($"'00{new string('x', 62)}…', ", formatted);
        Assert.EndsWith($"'09{new string('x', 62)}…', …", formatted);
    }

    [Fact]
    public void WhenALongNameIsShortenedWithinASurrogatePair_ThenThePairIsLeftOut()
    {
        // Arrange
        var name = new string('x', 63) + "😀" + "y";

        // Act
        var formatted = NameList.Format([name]);

        // Assert
        Assert.Equal($"'{new string('x', 63)}…'", formatted);
    }

    [Fact]
    public void WhenNoNameIsFormatted_ThenItIsNone()
    {
        // Act
        var formatted = NameList.Format([]);

        // Assert
        Assert.Equal("none", formatted);
    }
}
