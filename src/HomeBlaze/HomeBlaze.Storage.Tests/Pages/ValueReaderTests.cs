using HomeBlaze.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.Samples;
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

    private static Task<MarkdownPageFixture> CreateFixtureAsync() =>
        MarkdownPageFixture.CreateAsync(services =>
        {
            // The type registry reads the provider on first use, so the assemblies are added before anything is created.
            var typeProvider = (TypeProvider)services
                .Single(service => service.ServiceType == typeof(TypeProvider))
                .ImplementationInstance!;

            typeProvider
                .AddAssembly(typeof(Card).Assembly)
                .AddAssembly(typeof(Motor).Assembly);
        });

    private static async Task<(SubjectPathResolver Resolver, MarkdownFile Page)> AddPageAsync()
    {
        var fixture = await CreateFixtureAsync();
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
    public async Task WhenLabelIsNotGiven_ThenItIsTheTitleOrTheNameOfTheProperty()
    {
        // Arrange
        var (resolver, page) = await AddPageAsync();
        var reading = ValueReader.Read(resolver, page, "meter/Power", null, null);

        // Act
        var given = ValueReader.GetLabel(reading, "Leistung");
        var fromProperty = ValueReader.GetLabel(reading, null);
        var withoutReading = ValueReader.GetLabel(null, null);
        var fromTitle = ValueReader.GetLabel(ValueReader.Read(resolver, page, "meter/Flow", null, null), null);

        // Assert
        Assert.Equal("Leistung", given);
        Assert.Equal("Power", fromProperty);
        Assert.Equal("Flow rate", fromTitle);
        Assert.Equal(string.Empty, withoutReading);
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
