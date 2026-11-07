using System.Collections.Immutable;
using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionFieldPreviewTests
{
    private static readonly ImmutableArray<string> Variables = ["now", "value"];

    private sealed class Host
    {
        public int Validations { get; private set; }

        public int Previews { get; private set; }

        public double Gust { get; set; } = 9;

        public string? Validate(string text)
        {
            Validations++;
            return text.Contains('(') ? "expected ')'" : null;
        }

        public ExpressionPreview? Preview(string text)
        {
            Previews++;
            return text == "/Weather/Gust" ? new ExpressionPreview(null, Gust, Gust.ToString("0.#")) : new ExpressionPreview(Gust > 8, null, null);
        }
    }

    private static IReadOnlyList<ExpressionRow> Rows(string text)
    {
        ExpressionRows.TryParse(text, out var rows);
        return rows;
    }

    [Fact]
    public void WhenTheTextAndTheDelegatesAreUnchanged_ThenNothingIsEvaluatedAgain()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), '/', Variables, host.Validate, host.Preview);
        var (validations, previews) = (host.Validations, host.Previews);

        // Act
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), '/', Variables, host.Validate, host.Preview);

        // Assert
        Assert.Equal((validations, previews), (host.Validations, host.Previews));
        Assert.Equal("true", preview.ResultText);
        Assert.Equal(["now 9, true"], preview.RowTexts.ToArray());
    }

    [Fact]
    public void WhenTheTextChanges_ThenItsErrorIsShownAndNothingIsPreviewed()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), '/', Variables, host.Validate, host.Preview);
        var validations = host.Validations;

        // Act
        preview.Update("(/Weather/Gust > 8", [], '/', Variables, host.Validate, host.Preview);

        // Assert
        Assert.Equal(validations + 1, host.Validations);
        Assert.Equal("expected ')'", preview.Error);
        Assert.Null(preview.ResultText);
        Assert.Empty(preview.RowTexts);
    }

    [Fact]
    public void WhenTheValuesAreRefreshed_ThenAChangedValueIsReportedAndAnUnchangedOneIsNot()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();
        var rows = Rows("/Weather/Gust > 8");
        preview.Update("/Weather/Gust > 8", rows, '/', Variables, host.Validate, host.Preview);
        var validations = host.Validations;

        // Act
        var isUnchanged = !preview.RefreshValues(rows);
        host.Gust = 5;
        var isChanged = preview.RefreshValues(rows);

        // Assert
        Assert.True(isUnchanged);
        Assert.True(isChanged);
        Assert.Equal(validations, host.Validations);
        Assert.Equal("false", preview.ResultText);
        Assert.Equal(["now 5, false"], preview.RowTexts.ToArray());
    }

    [Fact]
    public void WhenThereIsNoPreview_ThenOnlyTheErrorAndTheCaseHintsAreShown()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();

        // Act
        preview.Update("/a > Now", Rows("/a > Now"), '/', Variables, host.Validate, preview: null);

        // Assert
        Assert.Null(preview.ResultText);
        Assert.Equal([string.Empty], preview.RowTexts.ToArray());
        Assert.Single(preview.Hints);
        Assert.False(preview.RefreshValues(Rows("/a > Now")));
    }

    [Fact]
    public void WhenTheHostHasHints_ThenTheyAreShownAndEvaluatedOnlyWhenTheTextChanges()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();
        var hintCount = 0;
        IReadOnlyList<string> GetHints(string text)
        {
            hintCount++;
            return text.Contains("==") ? ["Use >="] : [];
        }

        // Act
        preview.Update("/a == Now", Rows("/a == Now"), '/', Variables, host.Validate, host.Preview, GetHints);
        preview.RefreshValues(Rows("/a == Now"));
        preview.Update("/a == Now", Rows("/a == Now"), '/', Variables, host.Validate, host.Preview, GetHints);

        // Assert
        Assert.Equal(1, hintCount);
        Assert.Equal("Use >=", preview.Hints[0]);
        Assert.Equal(2, preview.Hints.Count);
    }
}
