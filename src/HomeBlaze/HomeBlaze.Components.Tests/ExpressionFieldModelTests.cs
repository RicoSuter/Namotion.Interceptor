using System.Collections.Immutable;
using HomeBlaze.Components.Inputs;
using Namotion.Interceptor.Expressions;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionFieldModelTests
{
    private static readonly ImmutableArray<string> Variables = ["now", "time", "event", "subject", "value"];

    [Fact]
    public void WhenTheExpressionIsSimple_ThenItStartsWithRows()
    {
        // Act
        var model = new ExpressionFieldModel("/a/B > 1");

        // Assert
        Assert.False(model.IsAdvanced);
        Assert.True(model.CanShowSimple);
        Assert.Single(model.Rows);
    }

    [Fact]
    public void WhenTheExpressionIsNotSimple_ThenItStaysAdvancedAndUnchanged()
    {
        // Arrange
        var model = new ExpressionFieldModel("(/a/B > 1)");

        // Act
        var isShownSimple = model.ShowSimple();

        // Assert
        Assert.False(isShownSimple);
        Assert.True(model.IsAdvanced);
        Assert.Equal("(/a/B > 1)", model.Text);
    }

    [Fact]
    public void WhenARowIsAddedAndFilled_ThenTheTextHasBothConditions()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1");

        // Act
        model.AddRow();
        model.SetRow(1, new ExpressionRow("or", "/c/D", "<", "2"));

        // Assert
        Assert.Equal("/a/B > 1 or /c/D < 2", model.Text);
    }

    [Fact]
    public void WhenTheFirstRowIsRemoved_ThenTheNextOneLosesItsCombinator()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1 and /c/D < 2");

        // Act
        model.RemoveRow(0);

        // Assert
        Assert.Equal("/c/D < 2", model.Text);
        Assert.Equal(string.Empty, model.Rows[0].Combinator);
    }

    [Fact]
    public void WhenASimpleTextIsLoadedInAdvancedMode_ThenTheModeStaysAdvanced()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1");
        model.ShowAdvanced();

        // Act
        model.Load("/c/D == 2");

        // Assert
        Assert.True(model.IsAdvanced);
        Assert.True(model.CanShowSimple);
        Assert.Equal("/c/D == 2", model.Rows[0].ToCondition());
    }

    [Fact]
    public void WhenTheTextIsEmpty_ThenTheFieldShowsNoRows()
    {
        // Act
        var model = new ExpressionFieldModel(null);

        // Assert
        Assert.False(model.IsAdvanced);
        Assert.Empty(model.Rows);
        Assert.Equal(string.Empty, model.Text);
    }

    [Theory]
    [InlineData("/a/B  >   1")]
    [InlineData("/a/B > 1 AND /c/D < 2")]
    [InlineData("/a/B == \"two words\"")]
    public void WhenAnExpressionIsLoaded_ThenItsTextIsNotRewritten(string text)
    {
        // Act
        var model = new ExpressionFieldModel(text);
        model.Load(text);

        // Assert
        Assert.Equal(text, model.Text);
    }

    [Theory]
    [InlineData("two words", "\"two words\"")]
    [InlineData("1 or /x == 2", "\"1 or /x == 2\"")]
    [InlineData("a)", "\"a)\"")]
    [InlineData("x | round", "\"x | round\"")]
    [InlineData("x|round", "\"x|round\"")]
    [InlineData("and", "\"and\"")]
    [InlineData("it's", "\"it's\"")]
    [InlineData("say \"hi\" now", "'say \"hi\" now'")]
    [InlineData("N/A", "\"N/A\"")]
    [InlineData("on/off", "\"on/off\"")]
    [InlineData("1/2", "\"1/2\"")]
    [InlineData("2024-01-01", "\"2024-01-01\"")]
    [InlineData("12:00", "\"12:00\"")]
    public void WhenAValueIsTypedThatWouldChangeTheMeaning_ThenItIsQuotedAsText(string typed, string expectedValue)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", typed));

        // Assert
        Assert.False(model.HasPendingRow);
        Assert.Equal($"/a/B == {expectedValue}", model.Text);
        AssertTextParsesToTheRows(model);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("-2.5")]
    [InlineData("20s")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("Open")]
    [InlineData("/c/D")]
    [InlineData("./c")]
    [InlineData("../c")]
    [InlineData("event/State")]
    [InlineData("subject/Name")]
    [InlineData("now")]
    [InlineData("\"two words\"")]
    [InlineData("-5")]
    [InlineData("-x")]
    [InlineData("-/c/D")]
    [InlineData("1.50")]
    public void WhenAValueIsTypedThatIsOneToken_ThenItIsWrittenAsItIs(string typed)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1", '/', Variables);

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", typed));

        // Assert
        Assert.Equal($"/a/B == {typed}", model.Text);
        AssertTextParsesToTheRows(model);
    }

    [Theory]
    [InlineData("1,5", "decimal point")]
    [InlineData("5abc", "Not a valid number")]
    [InlineData("it's \"x\"", "both kinds of quotes")]
    public void WhenAValueCannotBeWrittenAsTyped_ThenTheRowKeepsItAsPendingWithItsError(string typed, string errorPart)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", typed));

        // Assert
        Assert.Contains(errorPart, model.GetValueError(0));
        Assert.Equal("/a/B == 1", model.Text);
        Assert.Equal(typed, model.GetDisplayRow(0).Value);
        Assert.True(model.HasPendingRow);
    }

    [Theory]
    [InlineData("/a b")]
    [InlineData("/a)")]
    [InlineData("/a|x")]
    [InlineData("/a == /b")]
    [InlineData("and")]
    [InlineData("5")]
    [InlineData("-x")]
    [InlineData("-/a/B")]
    public void WhenAPathIsTypedThatIsNoPath_ThenTheRowKeepsItAsPendingWithItsError(string typed)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, typed, "==", "1"));

        // Assert
        Assert.StartsWith("Not a valid path", model.GetPathError(0));
        Assert.Equal("/a/B == 1", model.Text);
        Assert.Equal("/a/B", model.Rows[0].Path);
        Assert.Equal(typed, model.GetDisplayRow(0).Path);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/a/")]
    [InlineData("./")]
    [InlineData("/a/List[")]
    public void WhenAPathIsNotFinished_ThenItIsPendingWithoutAnError(string typed)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, typed, "==", "1"));

        // Assert
        Assert.Null(model.GetPathError(0));
        Assert.True(model.HasPendingRow);
        Assert.Equal("/a/B == 1", model.Text);
        Assert.Equal(typed, model.GetDisplayRow(0).Path);
    }

    [Fact]
    public void WhenARowIsRejected_ThenItsPendingEditAndErrorSurviveEditsOfOtherRows()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1 and /c/D == 2");
        model.SetRow(1, new ExpressionRow("and", "/c d", "==", "2"));

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", "3"));
        model.AddRow();

        // Assert
        Assert.StartsWith("Not a valid path", model.GetPathError(1));
        Assert.Equal("/c d", model.GetDisplayRow(1).Path);
        Assert.Equal("/a/B == 3 and /c/D == 2 and  == ", model.Text);
        Assert.True(model.HasPendingRow);
    }

    [Fact]
    public void WhenAPendingRowIsFixed_ThenItIsAppliedAndTheErrorIsCleared()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");
        model.SetRow(0, new ExpressionRow(string.Empty, "/a b", "==", "1"));

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/C", "==", "1"));

        // Assert
        Assert.Null(model.GetPathError(0));
        Assert.False(model.HasPendingRow);
        Assert.Equal("/a/C == 1", model.Text);
    }

    [Fact]
    public void WhenAPendingRowIsRemoved_ThenNothingStaysPending()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1 and /c/D == 2");
        model.SetRow(0, new ExpressionRow(string.Empty, "/a b", "==", "1"));

        // Act
        model.RemoveRow(0);

        // Assert
        Assert.False(model.HasPendingRow);
        Assert.Equal("/c/D == 2", model.Text);
    }

    [Fact]
    public void WhenARowIsStillBeingFilledIn_ThenItIsKeptAsItIs()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.AddRow();
        model.SetRow(1, new ExpressionRow("and", "/c/D", "==", string.Empty));

        // Assert
        Assert.False(model.HasPendingRow);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("/c/D", model.Rows[1].Path);
    }

    [Fact]
    public void WhenAValueStartsWithAVariableTheHostDoesNotDeclare_ThenItIsQuotedAsText()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", "event/State"));

        // Assert
        Assert.Equal("/a/B == \"event/State\"", model.Text);
        AssertTextParsesToTheRows(model);
    }

    private static void AssertTextParsesToTheRows(ExpressionFieldModel model)
    {
        Assert.True(ExpressionRows.TryParse(model.Text, out var parsed));
        Assert.Equal(model.Rows, parsed);
        Assert.True(ParsedExpression.TryParse(model.Text, '/', out _, out var error), error);
    }
}
