using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownMapPipeTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static async Task<MarkdownFile> AddPageAsync(string text)
    {
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        return await fixture.AddPageAsync("page.md", $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5 IsOn=true
            ::

            :: HomeBlaze.Components.ValueMap#onoff
            ::: Case Value=true Text="an"
            ::: Case Value=false Text="aus"
            ::

            :: HomeBlaze.Components.ValueMap#busy
            ::: Case When="subject/IsOn and value > 1000" Text="busy at {{ value }}"
            ::

            :: HomeBlaze.Components.ValueMap#defaultfirst
            ::: Case Text="always"
            ::: Case Value=true Text="never"
            ::

            :: HomeBlaze.Components.ValueMap#badwhen
            ::: Case When="value >" Color=Info
            ::

            {{{text}}}
            """);
    }

    private static RenderExpression GetExpression(MarkdownFile page) =>
        page.Children.Values.OfType<RenderExpression>().Single();

    [Theory]
    [InlineData("{{ meter/IsOn | map:onoff }}", "an")]
    [InlineData("{{ meter/Power > 2000 | map:onoff }}", "aus")]
    [InlineData("{{ meter/Power | map:busy }}", "busy at 1.23 kW")]
    [InlineData("{{ meter/Power | map:onoff }}", "1.23 kW")]
    [InlineData("{{ meter/Temperature | map:onoff }}", "")]
    [InlineData("{{ meter/IsOn | map:nothing }}", "Cannot resolve map: nothing")]
    public async Task WhenExpressionHasTheMapPipe_ThenTextIsThatOfTheMatchedCase(string text, string expected)
    {
        // Arrange
        var page = await AddPageAsync(text);

        // Act
        var result = GetExpression(page).GetText(null);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("{{ meter/IsOn | map:defaultfirst }}", "Case 1 has no Value and no When, so it matches every value and the cases after it never match. Make it the last case.")]
    [InlineData("{{ meter/IsOn | map:badwhen }}", "Case 1, When: expected a value after '>' at position 7")]
    public async Task WhenTheMapHasAMistake_ThenTextIsTheErrorOfTheMap(string text, string expected)
    {
        // Arrange
        var page = await AddPageAsync(text);

        // Act
        var result = GetExpression(page).GetText(null);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task WhenMapPipeHasNoName_ThenExpressionCarriesTheError()
    {
        // Arrange
        var page = await AddPageAsync("{{ meter/IsOn | map }}");

        // Act
        var expression = GetExpression(page);

        // Assert
        Assert.Equal("The pipe 'map' takes the name of a value map, such as map:wallbox", expression.Error);
    }

    [Fact]
    public async Task WhenEvaluatedContentIsReadWithTheMapPipe_ThenTextIsThatOfTheMatchedCase()
    {
        // Arrange
        var page = await AddPageAsync("Is {{ meter/IsOn | map:onoff }}");

        // Act
        var evaluated = page.ReadEvaluatedContent();

        // Assert
        Assert.EndsWith("Is an", evaluated!.TrimEnd());
    }
}
