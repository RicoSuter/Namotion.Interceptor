using System.Globalization;
using HomeBlaze.Abstractions;
using HomeBlaze.Storage.Files;
using HomeBlaze.Storage.Internal;
using HomeBlaze.Storage.Pages;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Recorder;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class MarkdownExpressionFormatTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static async Task<MarkdownFile> AddPageAsync(string text)
    {
        var fixture = await MarkdownPageFixture.CreateAsync();
        return await fixture.AddPageAsync("page.md", $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5 IsOn=true
            ::

            {{{text}}}
            """);
    }

    private static RenderExpression GetExpression(MarkdownFile page) =>
        page.Children.Values.OfType<RenderExpression>().Single();

    [Fact]
    public async Task WhenExpressionHasNoFormat_ThenTextIsTheDisplayValue()
    {
        // Arrange
        var page = await AddPageAsync("{{ meter/Power }}");

        // Act
        var text = GetExpression(page).GetText(null);

        // Assert
        Assert.Equal("1.23 kW", text);
    }

    [Fact]
    public async Task WhenFormatIsDisplay_ThenTextIsTheDisplayValue()
    {
        // Arrange
        var page = await AddPageAsync("{{ meter/Power | display }}");

        // Act
        var text = GetExpression(page).GetText(null);

        // Assert
        Assert.Equal("1.23 kW", text);
    }

    [Fact]
    public async Task WhenFormatIsRaw_ThenTextIsInvariant()
    {
        // Arrange
        var page = await AddPageAsync("{{ meter/Power | raw }}");
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-CH");
        try
        {
            // Act
            var text = GetExpression(page).GetText(null);

            // Assert
            Assert.Equal("1234.5", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task WhenValueIsMissing_ThenTextIsEmptyForBothFormats()
    {
        // Arrange
        var displayPage = await AddPageAsync("{{ meter/Temperature }}");
        var rawPage = await AddPageAsync("{{ meter/Temperature | raw }}");

        // Act
        var display = GetExpression(displayPage).GetText(null);
        var raw = GetExpression(rawPage).GetText(null);

        // Assert
        Assert.Equal(string.Empty, display);
        Assert.Equal(string.Empty, raw);
    }

    [Fact]
    public async Task WhenPathLeadsNowhere_ThenTextIsEmpty()
    {
        // Arrange
        var page = await AddPageAsync("{{ nothing/Power }}");

        // Act
        var text = GetExpression(page).GetText(null);

        // Assert
        Assert.Equal(string.Empty, text);
    }

    [Theory]
    [InlineData("{{ meter/Power }}")]
    [InlineData("{{ meter/Power | raw }}")]
    public async Task WhenTextIsReadWhileReadsAreRecorded_ThenThePropertyOfThePathIsRecorded(string text)
    {
        // Arrange
        var page = await AddPageAsync(text);
        var meter = ((IPathScope)page).TryGetNamedSubject("meter")!;
        var expression = GetExpression(page);
        var scope = ReadPropertyRecorder.Start();

        // Act
        expression.GetText(null);
        var recorded = scope.GetPropertiesAndDispose();

        // Assert: this record is what makes the page render again when the value changes
        Assert.Contains(new PropertyReference(meter, nameof(TestMeter.Power)), recorded);
    }

    [Fact]
    public async Task WhenFormatIsUnknown_ThenExpressionCarriesTheError()
    {
        // Arrange
        var page = await AddPageAsync("{{ meter/Power | rounded }}");

        // Act
        var expression = GetExpression(page);

        // Assert
        Assert.Equal("Unknown format 'rounded'", expression.Error);
    }

    [Fact]
    public async Task WhenFormatChangesOnReload_ThenExpressionIsReplaced()
    {
        // Arrange
        var fixture = await MarkdownPageFixture.CreateAsync();
        var page = await fixture.AddPageAsync("page.md", $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5
            ::

            {{ meter/Power }}
            """);

        // Act
        await fixture.UpdatePageAsync(page, $$$"""
            :: {{{Namespace}}}.TestMeter#meter Power=1234.5
            ::

            {{ meter/Power | raw }}
            """);

        // Assert
        Assert.Equal(ExpressionFormat.Raw, GetExpression(page).Format);
    }

    [Fact]
    public async Task WhenExpressionInATableEscapesTheSeparator_ThenTheFormatIsRead()
    {
        // Arrange
        var page = await AddPageAsync("""
            | Name | Power |
            | --- | --- |
            | Meter | {{ meter/Power \| raw }} |
            """);

        // Act
        var expression = GetExpression(page);
        var html = MarkdownPageFixture.Html(page.Children);

        // Assert
        Assert.Equal(ExpressionFormat.Raw, expression.Format);
        Assert.Equal("meter/Power", expression.Path);
        Assert.Contains("<table", html);
    }

    [Fact]
    public async Task WhenEvaluatedContentIsRead_ThenEachExpressionUsesItsFormat()
    {
        // Arrange
        var page = await AddPageAsync("A {{ meter/Power }} B {{ meter/Power | raw }} C {{ meter/Power | rounded }}");

        // Act
        var evaluated = page.ReadEvaluatedContent();

        // Assert
        Assert.Contains("A 1.23 kW B 1234.5 C {{ meter/Power | rounded }}", evaluated);
    }
}
