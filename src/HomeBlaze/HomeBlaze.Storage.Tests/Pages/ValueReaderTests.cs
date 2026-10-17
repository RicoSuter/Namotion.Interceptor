using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Services;
using HomeBlaze.Storage.Files;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Xunit;

namespace HomeBlaze.Storage.Tests.Pages;

[Collection(MarkdownPageFixture.StorageCollection)]
public class ValueReaderTests
{
    private static readonly string Namespace = typeof(TestMeter).Namespace!;

    private static readonly string Page = $$$"""
        :: {{{Namespace}}}.TestMeter#meter Power=1234.5 IsOn=true
        ::

        :: HomeBlaze.Components.ValueMap#onoff
        ::: Case Value=true Text="an" Color=Success Icon=Lightbulb
        ::: Case Value=False Color=Error
        ::: Case Value=TRUE Text="never"
        ::
        """;

    private static async Task<(SubjectPathResolver Resolver, MarkdownFile Page)> AddPageAsync()
    {
        var fixture = await MarkdownPageFixture.CreateWithWidgetsAndSamplesAsync();
        var page = await fixture.AddPageAsync("page.md", Page);
        return (fixture.Services.GetRequiredService<SubjectPathResolver>(), page);
    }

    [Fact]
    public async Task WhenPathHasAValueAndNoMap_ThenTextIsTheDisplayValue()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var reading = ValueReader.Read(resolver, page, "meter/Power", null, null);

        // Assert
        Assert.False(reading.IsMissing);
        Assert.Equal("1.23 kW", reading.Text);
        Assert.Equal(Color.Default, reading.Color);
        Assert.Null(reading.Icon);
        Assert.Null(reading.MapError);
    }

    [Fact]
    public async Task WhenMapHasACaseForTheValue_ThenTextColorAndIconComeFromTheFirstMatch()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var reading = ValueReader.Read(resolver, page, "meter/IsOn", "onoff", null);

        // Assert
        Assert.Equal("an", reading.Text);
        Assert.Equal(Color.Success, reading.Color);
        Assert.Equal("Lightbulb", reading.Icon);
    }

    [Fact]
    public async Task WhenMatchedCaseHasNoText_ThenTextIsTheDisplayValue()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var meter = (TestMeter)((IPathScope)page).TryGetNamedSubject("meter")!;
        meter.IsOn = false;

        // Act
        var reading = ValueReader.Read(resolver, page, "meter/IsOn", "onoff", null);

        // Assert
        Assert.Equal("No", reading.Text);
        Assert.Equal(Color.Error, reading.Color);
        Assert.Null(reading.Icon);
    }

    [Fact]
    public async Task WhenMapHasNoCaseForTheValue_ThenReadingIsAsWithoutAMap()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var reading = ValueReader.Read(resolver, page, "meter/Power", "onoff", null);

        // Assert
        Assert.Equal("1.23 kW", reading.Text);
        Assert.Equal(Color.Default, reading.Color);
        Assert.Null(reading.MapError);
    }

    [Theory]
    [InlineData("meter/Temperature")]
    [InlineData("meter/Missing")]
    [InlineData("nothing/Power")]
    [InlineData("")]
    [InlineData(null)]
    public async Task WhenValueIsMissingOrPathLeadsNowhere_ThenReadingIsMissing(string? path)
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var reading = ValueReader.Read(resolver, page, path, null, null);

        // Assert
        Assert.True(reading.IsMissing);
        Assert.Equal(ValueReader.MissingText, reading.Text);
        Assert.Null(reading.MapError);
    }

    [Theory]
    [InlineData("nomap")]
    [InlineData("meter")]
    public async Task WhenMapIsNoValueMap_ThenReadingHasAMapError(string map)
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var reading = ValueReader.Read(resolver, page, "meter/Power", map, null);

        // Assert
        Assert.Equal($"Cannot resolve map: {map}", reading.MapError);
    }

    [Theory]
    [InlineData("meter/Power[")]
    [InlineData("meter/Power/Extra")]
    [InlineData("[")]
    [InlineData("../../../x")]
    public async Task WhenPathIsMalformed_ThenReadingIsMissingAndNothingThrows(string malformed)
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var asPath = ValueReader.Read(resolver, page, malformed, null, null);
        var asMap = ValueReader.Read(resolver, page, "meter/Power", malformed, null);

        // Assert
        Assert.True(asPath.IsMissing);
        Assert.Equal(ValueReader.MissingText, asPath.Text);
        Assert.Equal($"Cannot resolve map: {malformed}", asMap.MapError);
    }

    [Fact]
    public async Task WhenValueIsReadAlone_ThenItIsTheValueOfTheProperty()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var value = ValueReader.ReadValue(resolver, page, "meter/Power");

        // Assert
        Assert.Equal(1234.5m, value);
    }

    [Theory]
    [InlineData("meter/Temperature")]
    [InlineData("nothing/Power")]
    [InlineData("meter/Power[")]
    [InlineData("")]
    [InlineData(null)]
    public async Task WhenValueIsReadAloneAndIsMissingOrPathLeadsNowhere_ThenItIsNull(string? path)
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var value = ValueReader.ReadValue(resolver, page, path);

        // Assert
        Assert.Null(value);
    }

    [Fact]
    public async Task WhenPropertyOfASubjectIsRead_ThenTextIsTheDisplayValue()
    {
        // Arrange
        var (_, page) = await AddPageAsync();
        var meter = ((IPathScope)page).TryGetNamedSubject("meter")!;

        // Act
        var reading = ValueReader.ReadProperty(meter, nameof(TestMeter.Power), null, null);

        // Assert
        Assert.False(reading.IsMissing);
        Assert.Equal("1.23 kW", reading.Text);
        Assert.Equal(nameof(TestMeter.Power), reading.Property?.Name);
        Assert.Null(reading.MapError);
    }

    [Fact]
    public async Task WhenPropertyOfASubjectIsReadWithAMap_ThenTheCaseApplies()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var meter = ((IPathScope)page).TryGetNamedSubject("meter")!;
        var valueMap = ValueReader.ResolveMap(resolver, page, "onoff", out _);

        // Act
        var reading = ValueReader.ReadProperty(meter, nameof(TestMeter.IsOn), valueMap, null);

        // Assert
        Assert.Equal("an", reading.Text);
        Assert.Equal(Color.Success, reading.Color);
        Assert.Equal("Lightbulb", reading.Icon);
    }

    [Theory]
    [InlineData("Temperature")]
    [InlineData("Missing")]
    [InlineData("")]
    [InlineData(null)]
    public async Task WhenPropertyOfASubjectIsNotSetOrUnknown_ThenReadingIsMissing(string? propertyName)
    {
        // Arrange
        var (_, page) = await AddPageAsync();
        var meter = ((IPathScope)page).TryGetNamedSubject("meter")!;

        // Act
        var reading = ValueReader.ReadProperty(meter, propertyName, null, null);

        // Assert
        Assert.True(reading.IsMissing);
        Assert.Equal(ValueReader.MissingText, reading.Text);
        Assert.Null(reading.MapError);
    }

    [Fact]
    public async Task WhenMapIsNoValueMap_ThenResolvingItGivesTheMapError()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();

        // Act
        var map = ValueReader.ResolveMap(resolver, page, "nomap", out var error);

        // Assert
        Assert.Null(map);
        Assert.Equal("Cannot resolve map: nomap", error);
    }

    [Fact]
    public async Task WhenPropertyOfASubjectIsReadWithAResolvedMap_ThenReadingEqualsThatOfThePath()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var meter = ((IPathScope)page).TryGetNamedSubject("meter")!;
        var valueMap = ValueReader.ResolveMap(resolver, page, "onoff", out _);

        // Act
        var fromSubject = ValueReader.ReadProperty(meter, nameof(TestMeter.IsOn), valueMap, null);
        var fromPath = ValueReader.Read(resolver, page, "meter/IsOn", "onoff", null);

        // Assert
        Assert.Equal(fromPath, fromSubject);
        Assert.Equal("an", fromSubject.Text);
    }

    [Fact]
    public async Task WhenResolvedPropertyIsRead_ThenReadingEqualsThatOfThePath()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var property = ValueReader.ResolveProperty(resolver, page, "meter/IsOn");
        var valueMap = ValueReader.ResolveMap(resolver, page, "onoff", out _);

        // Act
        var fromProperty = ValueReader.ReadResolved(property, valueMap, null);
        var fromPath = ValueReader.Read(resolver, page, "meter/IsOn", "onoff", null);
        var withoutProperty = ValueReader.ReadResolved(null, valueMap, null);

        // Assert
        Assert.Equal(fromPath, fromProperty);
        Assert.True(withoutProperty.IsMissing);
        Assert.Null(withoutProperty.MapError);
    }

    [Fact]
    public void WhenReadingIsTheMissingOne_ThenItShowsTheDashAndNoError()
    {
        // Act
        var reading = ValueReading.Missing;

        // Assert
        Assert.True(reading.IsMissing);
        Assert.Equal(ValueReader.MissingText, reading.Text);
        Assert.Equal(Color.Default, reading.Color);
        Assert.Null(reading.Property);
        Assert.Null(reading.Icon);
        Assert.Null(reading.MapError);
    }

    [Fact]
    public async Task WhenLabelIsNotGiven_ThenItIsTheTitleOrTheNameOfThePropertyOrThePath()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var reading = ValueReader.Read(resolver, page, "meter/Power", null, null);
        var missing = ValueReader.Read(resolver, page, "meter/Missing", null, null);

        // Act
        var given = ValueReader.GetLabel(reading.Property, "Leistung", "meter/Power");
        var fromProperty = ValueReader.GetLabel(reading.Property, null, "meter/Power");
        var fromTitle = ValueReader.GetLabel(ValueReader.Read(resolver, page, "meter/Flow", null, null).Property, null, "meter/Flow");
        var fromPath = ValueReader.GetLabel(missing.Property, null, "meter/Missing");
        var withoutPath = ValueReader.GetLabel(null, null, null);

        // Assert
        Assert.Equal("Leistung", given);
        Assert.Equal("Power", fromProperty);
        Assert.Equal("Flow rate", fromTitle);
        Assert.Equal("meter/Missing", fromPath);
        Assert.Equal(string.Empty, withoutPath);
    }

    [Fact]
    public async Task WhenIndicatorHasNoMap_ThenTrueIsPrimaryAndEverythingElseIsDefault()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var isOn = ValueReader.Read(resolver, page, "meter/IsOn", null, null);
        var power = ValueReader.Read(resolver, page, "meter/Power", null, null);
        var mapped = ValueReader.Read(resolver, page, "meter/IsOn", "onoff", null);

        // Act & Assert
        Assert.Equal(Color.Primary, ValueReader.GetIndicatorColor(isOn, null));
        Assert.Equal(Color.Default, ValueReader.GetIndicatorColor(power, null));
        Assert.Equal(Color.Success, ValueReader.GetIndicatorColor(mapped, "onoff"));
    }

    [Theory]
    [InlineData(0.5, 0, 1, 50)]
    [InlineData(30, 20, 40, 50)]
    [InlineData(2, 0, 1, 100)]
    [InlineData(-1, 0, 1, 0)]
    public void WhenValueIsANumber_ThenProgressIsItsShareOfTheRangeInPercent(double value, double minimum, double maximum, double expected)
    {
        // Act
        var percent = ValueReader.GetProgressPercent(value, minimum, maximum);

        // Assert
        Assert.Equal(expected, percent);
    }

    [Fact]
    public void WhenValueIsNoNumberOrRangeIsEmpty_ThenThereIsNoProgress()
    {
        // Act & Assert
        Assert.Null(ValueReader.GetProgressPercent(null, 0, 1));
        Assert.Null(ValueReader.GetProgressPercent(true, 0, 1));
        Assert.Null(ValueReader.GetProgressPercent("0.5", 0, 1));
        Assert.Null(ValueReader.GetProgressPercent(0.5, 1, 1));
        Assert.Null(ValueReader.GetProgressPercent(double.NaN, 0, 1));
        Assert.Null(ValueReader.GetProgressPercent(0.5, double.NaN, 1));
        Assert.Null(ValueReader.GetProgressPercent(0.5, 0, double.NaN));
        Assert.Equal(50, ValueReader.GetProgressPercent(0.5m, 0, 1));
        Assert.Equal(50, ValueReader.GetProgressPercent(5, 0, 10));
    }
}
