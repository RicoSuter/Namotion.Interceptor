using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Tests.Pages;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Storage.Tests;

public class RenderExpressionReadSetTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static async Task<MarkdownFile> AddPageAsync(string text)
    {
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        return await fixture.AddPageAsync("page.md", $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5 IsOn=true Temperature=21
            ::

            :: HomeBlaze.Components.ValueMap#state
            ::: Case When="subject/IsOn" Text="on"
            ::: Case When="subject/Temperature > 5" Text="warm"
            ::

            {{{text}}}
            """);
    }

    private static RenderExpression GetExpression(MarkdownFile page) =>
        page.Children.Values.OfType<RenderExpression>().Single();

    private static TestMeter GetMeter(MarkdownFile page) =>
        page.Children.Values.OfType<TestMeter>().Single();

    private static RegisteredSubjectProperty GetProperty(TestMeter meter, string name) =>
        meter.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    [Fact]
    public async Task WhenAnExpressionReadsAProperty_ThenTheReadSetContainsIt()
    {
        // Arrange
        var page = await AddPageAsync("{{ meter/Power }}");

        // Act
        var expression = GetExpression(page);

        // Assert
        Assert.Contains(GetProperty(GetMeter(page), nameof(TestMeter.Power)), expression.ReadProperties);
    }

    [Theory]
    [InlineData("{{ meter/Power | map:state }}")]
    [InlineData("{{ meter/Power | map:state | raw }}")]
    public async Task WhenACaseOfAValueMapReadsAProperty_ThenTheReadSetContainsIt(string text)
    {
        // Arrange
        var page = await AddPageAsync(text);
        var meter = GetMeter(page);

        // Act
        var readProperties = GetExpression(page).ReadProperties;

        // Assert
        Assert.Contains(GetProperty(meter, nameof(TestMeter.Power)), readProperties);
        Assert.Contains(GetProperty(meter, nameof(TestMeter.IsOn)), readProperties);
        Assert.DoesNotContain(GetProperty(meter, nameof(TestMeter.Temperature)), readProperties);
    }

    [Fact]
    public async Task WhenTheExpressionHasAnError_ThenTheReadSetIsEmpty()
    {
        // Arrange
        var page = await AddPageAsync("{{ meter/Power | nothing }}");

        // Act
        var expression = GetExpression(page);

        // Assert
        Assert.NotNull(expression.Error);
        Assert.Empty(expression.ReadProperties);
    }
}
