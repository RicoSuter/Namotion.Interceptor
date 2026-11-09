using System.Collections.Immutable;
using HomeBlaze.Components.Abstractions.Expressions;
using HomeBlaze.Components.Inputs;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionFieldPreviewTests
{
    private static readonly ImmutableArray<string> Variables = ["now", "value"];

    private sealed class Host
    {
        public int Validations { get; private set; }

        /// <summary>How often an expression or a path was prepared.</summary>
        public int Preparations { get; private set; }

        /// <summary>How often a prepared expression or path was evaluated.</summary>
        public int Evaluations { get; private set; }

        public double Gust { get; set; } = 9;

        public string? Validate(string text)
        {
            Validations++;
            return text.Contains('(') ? "expected ')'" : null;
        }

        public Func<ExpressionPreview>? Preview(string text)
        {
            Preparations++;
            return text.Contains('(') ? null : () => Evaluate(text);
        }

        private ExpressionPreview Evaluate(string text)
        {
            Evaluations++;
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
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), Variables, host.Validate, host.Preview);
        var (validations, preparations, evaluations) = (host.Validations, host.Preparations, host.Evaluations);

        // Act
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), Variables, host.Validate, host.Preview);

        // Assert
        Assert.Equal((validations, preparations, evaluations), (host.Validations, host.Preparations, host.Evaluations));
        Assert.Equal("true", preview.ResultText);
        Assert.Equal(["now 9, true"], preview.RowTexts.ToArray());
    }

    [Fact]
    public void WhenTheTextChanges_ThenItsErrorIsShownAndNothingIsPreviewed()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), Variables, host.Validate, host.Preview);
        var (validations, preparations) = (host.Validations, host.Preparations);

        // Act
        preview.Update("(/Weather/Gust > 8", [], Variables, host.Validate, host.Preview);

        // Assert
        Assert.Equal(validations + 1, host.Validations);
        Assert.Equal(preparations, host.Preparations);
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
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), Variables, host.Validate, host.Preview);
        var validations = host.Validations;

        // Act
        var isUnchanged = !preview.RefreshValues();
        host.Gust = 5;
        var isChanged = preview.RefreshValues();

        // Assert
        Assert.True(isUnchanged);
        Assert.True(isChanged);
        Assert.Equal(validations, host.Validations);
        Assert.Equal("false", preview.ResultText);
        Assert.Equal(["now 5, false"], preview.RowTexts.ToArray());
    }

    [Fact]
    public void WhenTheValuesAreRefreshed_ThenTheExpressionsArePreparedOnceAndOnlyEvaluatedAgain()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();
        preview.Update("/Weather/Gust > 8", Rows("/Weather/Gust > 8"), Variables, host.Validate, host.Preview);
        var (preparations, evaluations) = (host.Preparations, host.Evaluations);

        // Act
        preview.RefreshValues();
        preview.RefreshValues();

        // Assert
        Assert.Equal(3, preparations);
        Assert.Equal(preparations, host.Preparations);
        Assert.Equal(evaluations * 3, host.Evaluations);
    }

    [Fact]
    public void WhenARowHasNoValueYet_ThenItsTextShowsTheValueWithoutAResult()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();

        // Act
        preview.Update("/Weather/Gust > ", [new ExpressionRow(string.Empty, "/Weather/Gust", ">", string.Empty)], Variables, host.Validate, host.Preview);

        // Assert
        Assert.Equal(["now 9"], preview.RowTexts.ToArray());
    }

    [Fact]
    public void WhenThereIsNoPreview_ThenOnlyTheErrorAndTheCaseHintsAreShown()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();

        // Act
        preview.Update("/a > Now", Rows("/a > Now"), Variables, host.Validate, preview: null);

        // Assert
        Assert.Null(preview.ResultText);
        Assert.Equal([string.Empty], preview.RowTexts.ToArray());
        Assert.Single(preview.Hints);
        Assert.False(preview.RefreshValues());
    }

    [Fact]
    public void WhenTheHostHasHints_ThenTheyAreShownAndEvaluatedOnlyWhenTheTextChanges()
    {
        // Arrange
        var host = new Host();
        var preview = new ExpressionFieldPreview();
        var hintCount = 0;
        ImmutableArray<string> GetHints(string text)
        {
            hintCount++;
            return text.Contains("==") ? ["Use >="] : [];
        }

        // Act
        preview.Update("/a == Now", Rows("/a == Now"), Variables, host.Validate, host.Preview, GetHints);
        preview.RefreshValues();
        preview.Update("/a == Now", Rows("/a == Now"), Variables, host.Validate, host.Preview, GetHints);

        // Assert
        Assert.Equal(1, hintCount);
        Assert.Equal("Use >=", preview.Hints[0]);
        Assert.Equal(2, preview.Hints.Count);
    }

    [Fact]
    public void WhenTheValueAHintDependsOnChanges_ThenTheRefreshReportsTheChangedHints()
    {
        // Arrange
        object? limit = null;
        var preview = new ExpressionFieldPreview();
        preview.Update("/Weather/Gust > Limit", Rows("/Weather/Gust > Limit"), Variables, validate: null, _ => () => new ExpressionPreview(null, limit, null));
        var hintsWithoutValue = preview.Hints;

        // Act
        limit = 8.0;
        var isChanged = preview.RefreshValues();

        // Assert
        Assert.Empty(hintsWithoutValue);
        Assert.True(isChanged);
        Assert.Contains("never equals", Assert.Single(preview.Hints));
    }
}
