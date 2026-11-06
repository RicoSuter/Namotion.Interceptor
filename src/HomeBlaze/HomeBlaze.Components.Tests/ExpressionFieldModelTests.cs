using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionFieldModelTests
{
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
    public void WhenAValueIsTypedThatWouldChangeTheStructure_ThenItIsQuotedAsText(string typed, string expectedValue)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", typed));

        // Assert
        Assert.Null(model.Error);
        Assert.Equal($"/a/B == {expectedValue}", model.Text);
        Assert.True(ExpressionRows.TryParse(model.Text, out var parsed));
        Assert.Equal(model.Rows, parsed);
    }

    [Theory]
    [InlineData("5")]
    [InlineData("-2.5")]
    [InlineData("true")]
    [InlineData("/c/D")]
    [InlineData("now")]
    [InlineData("\"two words\"")]
    public void WhenAValueIsTypedThatIsOneToken_ThenItIsWrittenAsItIs(string typed)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", typed));

        // Assert
        Assert.Equal($"/a/B == {typed}", model.Text);
    }

    [Fact]
    public void WhenAValueHasBothKindsOfQuotes_ThenItIsRejectedAndTheTextStays()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/B", "==", "it's \"x\""));

        // Assert
        Assert.NotNull(model.Error);
        Assert.Equal("/a/B == 1", model.Text);
        Assert.Equal("1", model.Rows[0].Value);
    }

    [Theory]
    [InlineData("/a b")]
    [InlineData("/a)")]
    [InlineData("/a|x")]
    [InlineData("/a == /b")]
    [InlineData("and")]
    [InlineData("5")]
    public void WhenAPathIsTypedThatIsNoPath_ThenItIsRejectedAndTheTextStays(string typed)
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, typed, "==", "1"));

        // Assert
        Assert.NotNull(model.Error);
        Assert.Equal("/a/B == 1", model.Text);
        Assert.Equal("/a/B", model.Rows[0].Path);
    }

    [Fact]
    public void WhenARowIsEditedAfterARejection_ThenTheErrorIsCleared()
    {
        // Arrange
        var model = new ExpressionFieldModel("/a/B == 1");
        model.SetRow(0, new ExpressionRow(string.Empty, "/a b", "==", "1"));

        // Act
        model.SetRow(0, new ExpressionRow(string.Empty, "/a/C", "==", "1"));

        // Assert
        Assert.Null(model.Error);
        Assert.Equal("/a/C == 1", model.Text);
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
        Assert.Null(model.Error);
        Assert.Equal(2, model.Rows.Count);
        Assert.Equal("/c/D", model.Rows[1].Path);
    }
}
