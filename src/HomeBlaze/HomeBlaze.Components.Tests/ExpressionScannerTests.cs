using System.Text;
using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionScannerTests
{
    [Theory]
    [InlineData("/a/B == 1", "/a/B == 1")]
    [InlineData("/a == \"x [y\" or z", "/a ==  or z")]
    [InlineData("/Rooms[Bob's room]/T > 1", "/Rooms/T > 1")]
    [InlineData("'it''s' and x", " and x")]
    [InlineData("a ] b", "a ] b")]
    public void WhenAnExpressionIsScanned_ThenOnlyTheCharactersOutsideQuotesAndBracketsAreOutside(string text, string expected)
    {
        // Arrange
        var scanner = new ExpressionScanner();
        var outside = new StringBuilder();

        // Act
        foreach (var character in text)
        {
            if (scanner.Read(character))
            {
                outside.Append(character);
            }
        }

        // Assert
        Assert.Equal(expected, outside.ToString());
        Assert.False(scanner.IsOpen);
    }

    [Theory]
    [InlineData("/a == \"x")]
    [InlineData("/Rooms[Bob")]
    [InlineData("/Rooms[\"]\"")]
    public void WhenAQuoteOrABracketIsNotClosed_ThenTheScannerIsOpen(string text)
    {
        // Arrange
        var scanner = new ExpressionScanner();

        // Act
        foreach (var character in text)
        {
            scanner.Read(character);
        }

        // Assert
        Assert.True(scanner.IsOpen);
    }

    [Fact]
    public void WhenAnExpressionIsSplit_ThenWhiteSpaceInQuotesAndBracketsStaysInItsToken()
    {
        // Act
        var tokens = ExpressionTokens.Split("/Rooms[Bob's room]/T == \"a b\"");
        var unclosed = ExpressionTokens.Split("/Rooms[Bob's room");

        // Assert
        Assert.Equal(["/Rooms[Bob's room]/T", "==", "\"a b\""], tokens);
        Assert.Null(unclosed);
    }
}
