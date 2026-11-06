using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionRowsTests
{
    [Fact]
    public void WhenConditionsAreJoinedWithAndOrOr_ThenTheyAreRows()
    {
        // Act
        var isSimple = ExpressionRows.TryParse("/Weather/WindGust > 8 and /Living/Door/State == Open", out var rows);

        // Assert
        Assert.True(isSimple);
        Assert.Equal(
            [new ExpressionRow("", "/Weather/WindGust", ">", "8"), new ExpressionRow("and", "/Living/Door/State", "==", "Open")],
            rows.ToArray());
    }

    [Theory]
    [InlineData("/a/B > 8")]
    [InlineData("/a/B > 8 or /c/D <= 2 and /e/F != null")]
    [InlineData("/a/B == 'two words'")]
    [InlineData("Devices[my key]/IsOn == true")]
    [InlineData("Devices[a | b == (c)]/IsOn == true")]
    [InlineData("/a/B > 30m")]
    public void WhenRowsAreFormattedAgain_ThenTheTextIsTheSame(string text)
    {
        // Act
        var isSimple = ExpressionRows.TryParse(text, out var rows);

        // Assert
        Assert.True(isSimple);
        Assert.Equal(text, ExpressionRows.Format(rows));
    }

    [Theory]
    [InlineData("(/a/B > 1)")]
    [InlineData("not /a/B")]
    [InlineData("/a/B - 1 > 0")]
    [InlineData("/a/B > 1 and")]
    [InlineData("/a/B > 1 && /c/D < 2")]
    [InlineData("/a/B")]
    [InlineData("8 > /a/B")]
    [InlineData("/a/B == 'open")]
    [InlineData("now - ./LastTriggeredAt > 30m")]
    [InlineData("/a/B > 1 | round")]
    [InlineData("/a|b > 1")]
    [InlineData("/a/B == 'x''y'")]
    [InlineData("/a/B == b=c")]
    [InlineData("/a/B == !c")]
    [InlineData("/a/B == x\"y z\"")]
    [InlineData("'x' == /a/B")]
    [InlineData("true == /a/B")]
    [InlineData("/a/B > 1 and /c/D")]
    public void WhenTheExpressionHasAnotherForm_ThenItIsNotSimple(string text)
    {
        // Act
        var isSimple = ExpressionRows.TryParse(text, out _);

        // Assert
        Assert.False(isSimple);
    }

    [Fact]
    public void WhenTheExpressionIsEmpty_ThenItHasNoRows()
    {
        // Act
        var isSimple = ExpressionRows.TryParse("  ", out var rows);

        // Assert
        Assert.True(isSimple);
        Assert.Empty(rows);
    }

    [Fact]
    public void WhenACombinatorIsUppercase_ThenTheRowsWriteItInLowercase()
    {
        // Act
        ExpressionRows.TryParse("/a/B > 1 AND /c/D < 2", out var rows);

        // Assert
        Assert.Equal("/a/B > 1 and /c/D < 2", ExpressionRows.Format(rows));
    }
}
