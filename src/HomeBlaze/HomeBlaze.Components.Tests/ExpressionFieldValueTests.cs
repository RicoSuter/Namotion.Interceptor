using System.Collections.Immutable;
using Bunit;
using HomeBlaze.Components.Abstractions.Expressions;
using HomeBlaze.Components.Inputs;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ExpressionFieldValueTests : BunitContext
{
    public ExpressionFieldValueTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(TimeProvider.System);
        Services.AddScoped<PreviewTicker>();
    }

    [Fact]
    public void WhenThePreviewShowsValues_ThenTheResultIsTheTextOfTheValue()
    {
        // Arrange
        var preview = new ExpressionFieldPreview { ShowsValue = true };

        // Act
        preview.Update("/a/B * 2", [], ImmutableArray<string>.Empty, null, _ => () => new ExpressionPreview(null, 8.4m, "8.4"), null);

        // Assert
        Assert.Equal("8.4", preview.ResultText);
    }

    [Fact]
    public void WhenTheValueTextIsUnchanged_ThenRefreshingReportsNoChange()
    {
        // Arrange
        var preview = new ExpressionFieldPreview { ShowsValue = true };
        var value = 8.4m;
        // A new string on each read, as a host formats the value each time.
        preview.Update("/a/B * 2", [], ImmutableArray<string>.Empty, null, _ => () => new ExpressionPreview(null, value, value.ToString(System.Globalization.CultureInfo.InvariantCulture)), null);

        // Act
        var isChanged = preview.RefreshValues();

        // Assert
        Assert.False(isChanged);
        Assert.Equal("8.4", preview.ResultText);
    }

    [Fact]
    public void WhenThePreviewShowsConditions_ThenANumberIsUnknown()
    {
        // Arrange
        var preview = new ExpressionFieldPreview();

        // Act
        preview.Update("/a/B * 2", [], ImmutableArray<string>.Empty, null, _ => () => new ExpressionPreview(null, 8.4m, "8.4"), null);

        // Assert
        Assert.Equal("unknown", preview.ResultText);
    }

    [Fact]
    public void WhenTheFieldEditsAValue_ThenItShowsTheTextOfTheResultAndNoSimpleRows()
    {
        // Act
        var field = Render<ExpressionField>(parameters => parameters
            .Add(field => field.Kind, ExpressionFieldKind.Value)
            .Add(field => field.Value, "1 + 1")
            .Add(field => field.TestId, "field")
            .Add(field => field.Preview, (Func<string, Func<ExpressionPreview>?>)(_ => () => new ExpressionPreview(null, 2m, "2"))));

        // Assert
        Assert.Empty(field.FindAll("[data-testid=field-simple]"));
        Assert.Equal("Result now: 2", field.Find("[data-testid=field-result]").TextContent.Trim());
    }

    [Fact]
    public void WhenTextIsTypedIntoAValueField_ThenTheNewTextIsReported()
    {
        // Arrange
        string? reported = null;
        var field = Render<ExpressionField>(parameters => parameters
            .Add(field => field.Kind, ExpressionFieldKind.Value)
            .Add(field => field.TestId, "field")
            .Add(field => field.ValueChanged, (string? text) => reported = text));

        // Act
        field.Find("[data-testid=field-text]").Input("3 * 4");

        // Assert
        Assert.Equal("3 * 4", reported);
    }
}
