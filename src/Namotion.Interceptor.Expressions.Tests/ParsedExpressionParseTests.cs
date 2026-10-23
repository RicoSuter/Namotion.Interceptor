using Namotion.Interceptor.Expressions;

namespace Namotion.Interceptor.Expressions.Tests;

public class ParsedExpressionParseTests
{
    private static ParsedExpression Parse(string text)
    {
        Assert.True(ParsedExpression.TryParse(text, out var expression, out var error), error);
        return expression;
    }

    [Theory]
    [InlineData("motor/Speed", "motor/Speed")]
    [InlineData("  /Demo/Conveyor/CurrentSpeed  ", "/Demo/Conveyor/CurrentSpeed")]
    [InlineData("../notes-v2.md/motorstate", "../notes-v2.md/motorstate")]
    [InlineData("/Devices[a|b c]/Speed", "/Devices[a|b c]/Speed")]
    [InlineData("Vendor:Metadata", "Vendor:Metadata")]
    [InlineData("2024/Report.md/Title", "2024/Report.md/Title")]
    public void WhenTextIsOnePath_ThenSinglePathIsThatPath(string text, string path)
    {
        // Act
        var expression = Parse(text);

        // Assert
        Assert.Equal(path, expression.SinglePath);
        Assert.Equal([path], expression.Paths.ToArray());
        Assert.Empty(expression.Pipes);
    }

    [Fact]
    public void WhenExpressionHasPipes_ThenTheyAreReadInOrderWithTheirArguments()
    {
        // Act
        var expression = Parse("meter/Power | unit:kW | default:\"off\":2 |raw");

        // Assert
        Assert.Equal("meter/Power", expression.SinglePath);
        Assert.Equal(3, expression.Pipes.Length);
        Assert.Equal("unit", expression.Pipes[0].Name);
        Assert.Equal(["kW"], expression.Pipes[0].Arguments.ToArray());
        Assert.Equal("default", expression.Pipes[1].Name);
        Assert.Equal(["off", "2"], expression.Pipes[1].Arguments.ToArray());
        Assert.Equal("raw", expression.Pipes[2].Name);
        Assert.Empty(expression.Pipes[2].Arguments);
    }

    [Fact]
    public void WhenExpressionReadsSeveralPaths_ThenEachIsListedOnceInOrder()
    {
        // Act
        var expression = Parse("IsOn == true && (Temperature > 1 || IsOn == false) || not subject/IsCharging and Temperature < subject/Maximum");

        // Assert
        Assert.Null(expression.SinglePath);
        Assert.Equal(["IsOn", "Temperature", "subject/IsCharging", "subject/Maximum"], expression.Paths.ToArray());
    }

    [Theory]
    [InlineData("Name == Kitchen")]
    [InlineData("Day == monday")]
    [InlineData("Name == 'Kitchen'")]
    [InlineData("Name == \"Kitchen\"")]
    public void WhenBareWordFollowsAnOperator_ThenItIsTextAndNoPath(string text)
    {
        // Act
        var expression = Parse(text);

        // Assert
        Assert.Single(expression.Paths);
    }

    [Theory]
    [InlineData("value == subject")]
    [InlineData("Power > subject/Maximum")]
    [InlineData("Power > ./Maximum")]
    public void WhenRightOperandIsAVariableOrHasASlash_ThenItIsAPath(string text)
    {
        // Act
        var expression = Parse(text);

        // Assert
        Assert.Equal(2, expression.Paths.Length);
    }

    [Theory]
    [InlineData("", "expected a value at position 0")]
    [InlineData("Temperature >", "expected a value after '>' at position 13")]
    [InlineData("Temperature = 1", "use '==' for equality at position 12")]
    [InlineData("(Temperature > 1", "expected ')' at position 16")]
    [InlineData("Name == \"abc", "unterminated string at position 8")]
    [InlineData("Temperature > 1 Name == x", "unexpected 'N' at position 16")]
    [InlineData("Temperature > 1.2.3", "invalid number '1.2.3' at position 14")]
    [InlineData("a - b", "arithmetic is not supported at position 2")]
    [InlineData("a * 2", "arithmetic is not supported at position 2")]
    [InlineData("a + b", "arithmetic is not supported at position 2")]
    [InlineData("a / 2", "arithmetic is not supported at position 2")]
    [InlineData("a and", "expected a value at position 5")]
    [InlineData("a == and", "expected a value after '==' at position 5")]
    [InlineData("a |", "expected a pipe name after '|' at position 3")]
    [InlineData("a | unit:", "expected an argument after ':' at position 9")]
    public void WhenExpressionIsInvalid_ThenErrorHasMessageAndPosition(string text, string expectedError)
    {
        // Act
        var success = ParsedExpression.TryParse(text, out var expression, out var error);

        // Assert
        Assert.False(success);
        Assert.Null(expression);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void WhenExpressionIsLongerThanLimit_ThenReturnsLengthError()
    {
        // Arrange
        var text = "a == " + new string('1', 1000);

        // Act
        var success = ParsedExpression.TryParse(text, out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Equal("expression is longer than 1000 characters at position 1000", error);
    }

    [Fact]
    public void WhenNestingExceedsLimit_ThenReturnsNestingError()
    {
        // Arrange
        var text = new string('(', 33) + "a" + new string(')', 33);

        // Act
        var success = ParsedExpression.TryParse(text, out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Equal("expression is nested too deeply at position 32", error);
    }

    [Fact]
    public void WhenNotIsRepeatedBeyondLimit_ThenReturnsNestingError()
    {
        // Arrange
        var text = new string('!', 40) + "a";

        // Act
        var success = ParsedExpression.TryParse(text, out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Equal("expression is nested too deeply at position 32", error);
    }

    [Theory]
    [InlineData("not IsCharging")]
    [InlineData("!IsCharging")]
    [InlineData("NOT(IsCharging)")]
    [InlineData("a != b")]
    [InlineData("a>=1 AND b<=2 Or c")]
    [InlineData("note/Text")]
    [InlineData("-5 < Temperature")]
    [InlineData("Temperature > .5")]
    [InlineData("a == null")]
    [InlineData("a == TRUE")]
    public void WhenExpressionIsValid_ThenItParses(string text)
    {
        // Act & Assert
        Parse(text);
    }
}
