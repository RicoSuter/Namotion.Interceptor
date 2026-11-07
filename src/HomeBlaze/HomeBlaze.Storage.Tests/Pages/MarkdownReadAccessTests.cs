using System.Reactive.Concurrency;
using System.Reactive.Linq;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Storage.Tests.Pages;

public class MarkdownReadAccessTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static async Task<(MarkdownPageFixture Fixture, MarkdownFile Page)> AddPageAsync(string text)
    {
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5 IsOn=true
            ::

            :: HomeBlaze.Components.ValueMap#busy
            ::: Case When="subject/IsOn and value > 1000" Text="busy at {{ value }}"
            ::

            {{{text}}}
            """);

        return (fixture, page);
    }

    private static string GetLastLine(string? content) => content!.TrimEnd().Split('\n')[^1];

    [Fact]
    public async Task WhenNoScopeIsActive_ThenTheExpressionIsRendered()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: {{ meter/Power }}");

        // Act
        var evaluated = page.ReadEvaluatedContent();

        // Assert
        Assert.Equal("Power: 1.23 kW", GetLastLine(evaluated));
    }

    [Fact]
    public async Task WhenTheScopeRefusesTheProperty_ThenTheExpressionRendersEmpty()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: {{ meter/Power }}, on: {{ meter/IsOn }}");

        // Act
        string? evaluated;
        using (PropertyReadAccess.Enter(property => property.Name != nameof(TestMeter.Power)))
        {
            evaluated = page.ReadEvaluatedContent();
        }

        // Assert
        Assert.Equal("Power: , on: Yes", GetLastLine(evaluated));
    }

    [Fact]
    public async Task WhenTheScopeAllowsTheProperty_ThenTheExpressionIsRendered()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: {{ meter/Power }}");

        // Act
        string? evaluated;
        using (PropertyReadAccess.Enter(property => property.Name == nameof(TestMeter.Power)))
        {
            evaluated = page.ReadEvaluatedContent();
        }

        // Assert
        Assert.Equal("Power: 1.23 kW", GetLastLine(evaluated));
    }

    [Fact]
    public async Task WhenTheScopeIsDisposed_ThenTheCheckOfTheFlowBeforeIsRestored()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: [{{ meter/Power }}]");

        // Act
        string? evaluatedInInnerScope;
        string? evaluatedInOuterScope;
        using (PropertyReadAccess.Enter(_ => true))
        {
            using (PropertyReadAccess.Enter(_ => false))
            {
                evaluatedInInnerScope = page.ReadEvaluatedContent();
            }

            evaluatedInOuterScope = page.ReadEvaluatedContent();
        }

        var evaluatedWithoutScope = page.ReadEvaluatedContent();

        // Assert
        Assert.Equal("Power: []", GetLastLine(evaluatedInInnerScope));
        Assert.Equal("Power: [1.23 kW]", GetLastLine(evaluatedInOuterScope));
        Assert.Equal("Power: [1.23 kW]", GetLastLine(evaluatedWithoutScope));
    }

    [Fact]
    public async Task WhenTheScopeRefusesTheProperty_ThenItsSourcePathIsLeftOut()
    {
        // Arrange
        var (_, page) = await AddPageAsync("Power: {{ meter/Power }}, on: {{ meter/IsOn }}");

        // Act
        string? evaluated;
        using (PropertyReadAccess.Enter(property => property.Name != nameof(TestMeter.Power)))
        {
            evaluated = page.ReadEvaluatedContent(includeSourcePaths: true);
        }

        var evaluatedWithoutScope = page.ReadEvaluatedContent(includeSourcePaths: true);

        // Assert
        Assert.Equal("Power: , on: Yes (/page.md/meter/IsOn)", GetLastLine(evaluated));
        Assert.Equal("Power: 1.23 kW (/page.md/meter/Power), on: Yes (/page.md/meter/IsOn)", GetLastLine(evaluatedWithoutScope));
    }

    [Fact]
    public async Task WhenTheScopeRefusesAPropertyThatAValueMapReads_ThenTheCaseDoesNotSeeIt()
    {
        // Arrange
        var (_, page) = await AddPageAsync("State: {{ meter/Power | map:busy }}");

        // Act
        string? evaluated;
        using (PropertyReadAccess.Enter(property => property.Name != nameof(TestMeter.IsOn)))
        {
            evaluated = page.ReadEvaluatedContent();
        }

        var evaluatedWithoutScope = page.ReadEvaluatedContent();

        // Assert
        Assert.Equal("State: 1.23 kW", GetLastLine(evaluated));
        Assert.Equal("State: busy at 1.23 kW", GetLastLine(evaluatedWithoutScope));
    }

    [Fact]
    public async Task WhenTheScopeRefusesTheProperty_ThenTheShownTextOfTheExpressionIsEmpty()
    {
        // Arrange
        var (_, page) = await AddPageAsync("{{ meter/Power }}");
        var expression = page.Children.Values.OfType<RenderExpression>().Single();

        // Act
        string text;
        using (PropertyReadAccess.Enter(_ => false))
        {
            text = expression.GetText(null);
        }

        // Assert
        Assert.Equal(string.Empty, text);
        Assert.Equal("1.23 kW", expression.GetText(null));
    }

    [Fact]
    public async Task WhenAPropertyChangesWithinAScopeThatRefusesIt_ThenTheSharedValueOfTheExpressionStillFollowsIt()
    {
        // Arrange
        var (_, page) = await AddPageAsync("{{ meter/Power }}");
        var expression = page.Children.Values.OfType<RenderExpression>().Single();
        var meter = page.Children.Values.OfType<TestMeter>().Single();
        var values = new List<object?>();
        using var subscription = ((IInterceptorSubject)page).Context.GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change => ReferenceEquals(change.Property.Subject, expression) &&
                             change.Property.Name == nameof(RenderExpression.Value))
            .Subscribe(change => values.Add(change.GetNewValue<object?>()));

        // Act
        using (PropertyReadAccess.Enter(_ => false))
        {
            meter.Power = 10m;
        }

        meter.Power = 20m;

        // Assert
        Assert.Equal([10m, 20m], values);
    }
}
