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
    public void WhenARemovedRowIsRemovedAgain_ThenNoOtherRowIsRemoved()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1 and /c/D < 2 and /e/F == 3");
        var middle = model.GetRowKey(1);
        var last = model.GetRowKey(2);

        // Act
        var isRemoved = model.RemoveRow(middle);
        var isRemovedAgain = model.RemoveRow(middle);
        model.RemoveRow(last);
        var isLastRemovedAgain = model.RemoveRow(last);

        // Assert
        Assert.True(isRemoved);
        Assert.False(isRemovedAgain);
        Assert.False(isLastRemovedAgain);
        Assert.Equal("/a/B > 1", model.Text);
    }

    [Fact]
    public void WhenARowThatIsGoneIsEdited_ThenNothingChanges()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1 and /c/D < 2");
        var removed = model.GetRowKey(0);
        model.RemoveRow(removed);

        // Act
        var isChanged = model.SetRow(removed, new ExpressionRow(string.Empty, "/x/Y", "==", "5"));

        // Assert
        Assert.False(isChanged);
        Assert.Equal("/c/D < 2", model.Text);
        Assert.False(model.HasPendingRow);
    }

    [Fact]
    public void WhenRowsAreAddedOrRemoved_ThenEachRowKeepsItsKey()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1 and /c/D < 2");
        var second = model.GetRowKey(1);

        // Act
        model.AddRow();
        model.RemoveRow(model.GetRowKey(0));

        // Assert
        Assert.Same(second, model.GetRowKey(0));
        Assert.NotSame(second, model.GetRowKey(1));
    }

    [Fact]
    public void WhenTheTextIsShownWhileARowIsPendingOrNotFinished_ThenTheyAreDroppedAndThatIsReported()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", "1,5"));
        model.AddRow();

        // Act
        var isDropped = model.ShowAdvanced();
        model.ShowSimple();

        // Assert
        Assert.True(isDropped);
        Assert.False(model.HasPendingRow);
        Assert.Equal("/a/B == 1", model.Text);
        Assert.Equal(new ExpressionRow(string.Empty, "/a/B", "==", "1"), Assert.Single(model.Rows));
    }

    [Fact]
    public void WhenTheTextIsShownWithoutAPendingRow_ThenNothingIsDropped()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1 and /c/D == 2");

        // Act
        var isDropped = model.ShowAdvanced();

        // Assert
        Assert.False(isDropped);
        Assert.True(model.IsAdvanced);
        Assert.Equal(2, model.Rows.Count);
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
    public void WhenTheOwnerPassesItsValueAgainWhileARowIsNotFinished_ThenTheRowStaysSimpleWithTheEdit()
    {
        // Arrange
        var model = new ExpressionFieldModel(string.Empty);
        model.AddRow();
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", string.Empty));
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", ">", string.Empty));

        // Act
        model.SetValue(string.Empty);
        model.SetValue(model.Text);

        // Assert
        Assert.False(model.IsAdvanced);
        Assert.Equal(new ExpressionRow(string.Empty, "/a/B", ">", string.Empty), Assert.Single(model.Rows));
        Assert.Equal(string.Empty, model.Text);
    }

    [Fact]
    public void WhenTheOwnerPassesItsValueFromBeforeAnEditWhileARowIsPending_ThenThePendingEditIsKept()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1");
        model.AddRow();
        model.SetValue(model.Text);
        model.SetRow(1, new ExpressionRow("and", "/c/D", "<", "2"));
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", ">", "1,5"));

        // Act
        model.SetValue("/a/B > 1");

        // Assert
        Assert.True(model.HasPendingRow);
        Assert.Equal("1,5", model.GetDisplayRow(0).Value);
        Assert.Equal("/a/B > 1 and /c/D < 2", model.Text);
    }

    [Fact]
    public void WhenTheOwnerPassesAnotherSimpleValue_ThenItsRowsAreShown()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1");

        // Act
        model.SetValue("/c/D < 2");

        // Assert
        Assert.False(model.IsAdvanced);
        Assert.Equal("/c/D < 2", Assert.Single(model.Rows).ToCondition());
    }

    [Theory]
    [InlineData("(/a/B > 1)")]
    [InlineData("/a/B > ")]
    public void WhenTheOwnerPassesAValueTheRowsCannotWriteAgain_ThenItIsShownAdvancedAndUnchanged(string value)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B > 1");

        // Act
        model.SetValue(value);

        // Assert
        Assert.True(model.IsAdvanced);
        Assert.False(model.CanShowSimple);
        Assert.Equal(value, model.Text);
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
    [InlineData("event/State", "\"event/State\"")]
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
        var model = new ExpressionFieldModel("/a/B == 1") { Variables = Variables };

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
        Assert.Contains(errorPart, model.GetPendingRow(0)?.ValueError);
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
        Assert.StartsWith("Not a valid path", model.GetPendingRow(0)?.PathError);
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
        Assert.Null(model.GetPendingRow(0)?.PathError);
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
        Assert.StartsWith("Not a valid path", model.GetPendingRow(1)?.PathError);
        Assert.Equal("/c d", model.GetDisplayRow(1).Path);
        Assert.Equal("/a/B == 3 and /c/D == 2", model.Text);
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
        Assert.Null(model.GetPendingRow(0)?.PathError);
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

    [Theory]
    [InlineData("/c/D", "")]
    [InlineData("", "2")]
    [InlineData("", "")]
    public void WhenARowIsStillBeingFilledIn_ThenItStaysOutOfTheTextAndCountsAsPending(string path, string value)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.AddRow();
        var isChanged = model.SetRow(1, new ExpressionRow("and", path, "==", value));

        // Assert
        Assert.False(isChanged);
        Assert.True(model.HasPendingRow);
        Assert.Equal("/a/B == 1", model.Text);
        Assert.Equal(new ExpressionRow("and", path, "==", value), model.Rows[1]);
    }

    [Fact]
    public void WhenTheFirstRowIsNotFinished_ThenTheTextStartsWithTheNextRowWithoutItsCombinator()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1 or /c/D == 2");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", string.Empty));

        // Assert
        Assert.Equal("/c/D == 2", model.Text);
        Assert.Equal(2, model.Rows.Count);
        Assert.True(model.HasPendingRow);
    }

    [Fact]
    public void WhenAnUnfinishedRowIsRemoved_ThenTheTextIsUnchanged()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");
        model.AddRow();

        // Act
        var isChanged = model.RemoveRow(model.GetRowKey(1));

        // Assert
        Assert.False(isChanged);
        Assert.False(model.HasPendingRow);
        Assert.Equal("/a/B == 1", model.Text);
    }

    [Fact]
    public void WhenAnUnfinishedRowIsCompleted_ThenNothingIsPending()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");
        model.AddRow();

        // Act
        model.SetRow(1, new ExpressionRow("and", "/c/D", ">", "2"));

        // Assert
        Assert.False(model.HasPendingRow);
        Assert.Equal("/a/B == 1 and /c/D > 2", model.Text);
    }

    [Fact]
    public void WhenAnEditOfARowDoesNotChangeTheText_ThenSetRowSaysSo()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        var isUnfinishedPathChange = model.SetRow(0, new ExpressionRow(string.Empty, "/a/", "==", "1"));
        var isInvalidValueChange = model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", "1,5"));
        var isSameRowChange = model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", "1"));
        var isAppliedChange = model.SetRow(0, new ExpressionRow(string.Empty, "/a/C", "==", "1"));

        // Assert
        Assert.False(isUnfinishedPathChange);
        Assert.False(isInvalidValueChange);
        Assert.False(isSameRowChange);
        Assert.True(isAppliedChange);
        Assert.Equal("/a/C == 1", model.Text);
    }

    [Theory]
    [InlineData("Sibling/Value")]
    [InlineData("Devices[x]/IsOn")]
    [InlineData("-Sibling/Value")]
    [InlineData("N/A")]
    public void WhenTheOperatorOfARowWithALoadedBarePathValueChanges_ThenTheValueIsWrittenAsItIs(string value)
    {
        // Arrange
        var model = new ExpressionFieldModel($"/a/B == {value}");

        // Act
        model.SetRow(0, model.Rows[0] with { Operator = "!=" });

        // Assert
        Assert.False(model.HasPendingRow);
        Assert.Equal($"/a/B != {value}", model.Text);
    }

    [Fact]
    public void WhenALoadedRowIsAppliedAgain_ThenItsValueIsWrittenAsItIs()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == Sibling/Value and /c/D == N/A");

        // Act
        model.SetRow(0, model.Rows[0]);
        model.SetRow(1, model.Rows[1] with { Combinator = "or" });

        // Assert
        Assert.Equal("/a/B == Sibling/Value or /c/D == N/A", model.Text);
    }

    private static void AssertTextParsesToTheRows(ExpressionFieldModel model)
    {
        Assert.True(ExpressionRows.TryParse(model.Text, out var parsed));
        Assert.Equal(model.Rows, parsed);
        Assert.True(ParsedExpression.TryParse(model.Text, '/', out _, out var error), error);
    }
}
