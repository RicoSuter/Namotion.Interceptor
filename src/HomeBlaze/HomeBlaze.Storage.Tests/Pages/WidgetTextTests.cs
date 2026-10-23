using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Tracking.Recorder;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class WidgetTextTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static readonly string Page = $$$"""
        :: {{{Namespace}}}.TestMeter#meter Power=1234.5 IsOn=true
        ::

        :: {{{Namespace}}}.TestCharger#wallbox IsPluggedIn=true IsCharging=true ChargingPower=7400
        ::

        :: HomeBlaze.Components.ValueMap#charging
        ::: Case When="subject/IsCharging" Text="Charging {{ subject/ChargingPower }}"
        ::: Case Text="Free"
        ::

        :: HomeBlaze.Components.ValueMap#badwhen
        ::: Case When="value >" Text="never"
        ::
        """;

    private static async Task<(SubjectPathResolver Resolver, MarkdownFile Page)> AddPageAsync()
    {
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", Page);
        return (fixture.Services.GetRequiredService<SubjectPathResolver>(), page);
    }

    private static string? Render(SubjectPathResolver resolver, MarkdownFile page, string? text, string? path, out string? error) =>
        new WidgetText("Label", ExpressionUse.WidgetText).Render(text, resolver, page, path, null, out error);

    [Theory]
    [InlineData("{{ value }}", "wallbox/ChargingPower", "7.4 kW")]
    [InlineData("Wallbox {{ subject/ChargingPower }}", "wallbox/IsCharging", "Wallbox 7.4 kW")]
    [InlineData("{{ subject/ChargingPower | raw }} W", "wallbox", "7400 W")]
    [InlineData("{{ meter/Power }}", null, "1.23 kW")]
    [InlineData("{{ value | map:charging }}", "wallbox/IsCharging", "Charging 7.4 kW")]
    [InlineData("{{ subject | map:charging }}", "wallbox", "Charging 7.4 kW")]
    [InlineData("{{ subject/IsCharging and value > 1000 }}", "wallbox/ChargingPower", "Yes")]
    [InlineData("[{{ value }}]", "nothing/Here", "[]")]
    public async Task WhenTextHasExpressions_ThenTheyShowTheValuesOfThePathAndThePage(string text, string? path, string expected)
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var rendered = Render(resolver, page, text, path, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(expected, rendered);
    }

    [Theory]
    [InlineData("{{ value > }}", "Label: expected a value after '>' at position 7")]
    [InlineData("{{ value | rounded }}", "Label: Unknown pipe 'rounded'")]
    [InlineData("Wallbox {{ value", "Label: '{{' is not closed at position 8")]
    public async Task WhenTextHasAMistake_ThenTheErrorNamesTheArgumentAndThereIsNoText(string text, string expected)
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var rendered = Render(resolver, page, text, "wallbox/ChargingPower", out var error);

        // Assert
        Assert.Null(rendered);
        Assert.Equal(expected, error);
    }

    [Fact]
    public async Task WhenWidgetHasNoPathAndTextUsesAVariable_ThenItIsAnError()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var title = new WidgetText("Title", ExpressionUse.WidgetTextWithoutPath);

        // Act
        var rendered = title.Render("{{ subject/ChargingPower }}", resolver, page, default(ValueBinding), null, out var error);

        // Assert
        Assert.Null(rendered);
        Assert.Equal("Title: 'subject' is not available in a widget without a Path", error);
    }

    [Fact]
    public async Task WhenMapOfThePipeHasAMistake_ThenTextIsItsMessage()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var rendered = Render(resolver, page, "{{ value | map:badwhen }}", "wallbox/IsCharging", out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("Case 1, When: expected a value after '>' at position 7", rendered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Wallbox")]
    [InlineData("a } b { c")]
    public async Task WhenTextHasNoExpression_ThenItIsReturnedAsWritten(string? text)
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var rendered = Render(resolver, page, text, "wallbox/IsCharging", out var error);

        // Assert
        Assert.Null(error);
        Assert.Same(text, rendered);
    }

    [Fact]
    public async Task WhenTextHasNoExpression_ThenRenderAllocatesNothing()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var label = new WidgetText("Label", ExpressionUse.WidgetText);
        label.Render("Wallbox", resolver, page, "wallbox/IsCharging", null, out _);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        label.Render("Wallbox", resolver, page, "wallbox/IsCharging", null, out _);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task WhenTextIsRendered_ThenEveryPropertyItReadsIsRecorded()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var wallbox = ((IPathScope)page).TryGetNamedSubject("wallbox")!;
        var meter = ((IPathScope)page).TryGetNamedSubject("meter")!;
        var label = new WidgetText("Label", ExpressionUse.WidgetText);
        var scope = ReadPropertyRecorder.Start();

        // Act
        label.Render("{{ subject/ChargingPower }} of {{ meter/Power }}", resolver, page, "wallbox/IsCharging", null, out _);
        var recorded = scope.GetPropertiesAndDispose();

        // Assert: these records are what make the widget render again when one of the values changes
        Assert.Contains(new PropertyReference(wallbox, nameof(TestCharger.ChargingPower)), recorded);
        Assert.Contains(new PropertyReference(meter, nameof(TestMeter.Power)), recorded);
    }

    [Fact]
    public async Task WhenTextChanges_ThenTheNewTextIsParsed()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var label = new WidgetText("Label", ExpressionUse.WidgetText);
        label.Render("{{ value | raw }}", resolver, page, "wallbox/ChargingPower", null, out _);

        // Act
        var rendered = label.Render("{{ value }}", resolver, page, "wallbox/ChargingPower", null, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("7.4 kW", rendered);
    }

    [Fact]
    public async Task WhenReadingIsRead_ThenItsBindingHasTheSubjectOfThePath()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var wallbox = ((IPathScope)page).TryGetNamedSubject("wallbox")!;

        // Act
        var property = ValueReader.Read(resolver, page, "wallbox/ChargingPower", null, null);
        var subject = ValueReader.Read(resolver, page, "wallbox", "charging", null);
        var nowhere = ValueReader.Read(resolver, page, "nothing/Here", null, null);

        // Assert
        Assert.Same(wallbox, property.Binding.Subject);
        Assert.Equal(7400m, property.Binding.Value);
        Assert.Same(wallbox, subject.Binding.Subject);
        Assert.Null(subject.Binding.Value);
        Assert.True(nowhere.Binding.IsEmpty);
    }
}
