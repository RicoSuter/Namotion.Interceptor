using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Hyundai.Mapping;
using Namotion.Devices.Hyundai.Tests.Testing;
using Namotion.Interceptor.Testing;

namespace Namotion.Devices.Hyundai.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BluelinkApiChangesCollection
{
    public const string Name = "Bluelink API changes";

    private BluelinkApiChangesCollection()
    {
    }
}

/// <summary>
/// The warnings for answers that suggest an API change. What was logged is shared by the process, so this class runs
/// alone and starts every test with nothing logged.
/// </summary>
[Collection(BluelinkApiChangesCollection.Name)]
public class BluelinkStatusParserLoggingTests
{
    public BluelinkStatusParserLoggingTests() => BluelinkApiChanges.Reset();

    /// <summary>Parses the fixture twice, with the value at the dotted path set; a null path keeps the fixture unchanged.</summary>
    private static RecordingLogger ParseFixtureTwice(string? path = null, JsonNode? value = null)
    {
        var vehicle = JsonNode.Parse(FixtureLoader.Load("carstatus-legacy-ev.json"))!["resMsg"]!["state"]!["Vehicle"]!;
        if (path is not null)
        {
            var segments = path.Split('.');
            var parent = segments[..^1].Aggregate(vehicle, (node, segment) => node[segment] ??= new JsonObject());
            parent[segments[^1]] = value;
        }

        return ParseTwice(vehicle.ToJsonString());
    }

    private static RecordingLogger ParseTwice(string vehicleJson)
    {
        var logger = new RecordingLogger();
        using var document = JsonDocument.Parse(vehicleJson);
        BluelinkStatusParser.Parse(document.RootElement, logger);
        BluelinkStatusParser.Parse(document.RootElement, logger);
        return logger;
    }

    [Fact]
    public void WhenStatusIsARealCapture_ThenNothingIsLogged()
    {
        // Act
        var logger = ParseFixtureTwice();

        // Assert
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void WhenCoreFieldsAreMissing_ThenEachIsLoggedOnce()
    {
        // Act
        var logger = ParseTwice("{}");

        // Assert
        Assert.Equal(
            [
                "Bluelink API changed? The status has no Cabin.Door.Row1.Driver.Lock.",
                "Bluelink API changed? The status has no Date.",
                "Bluelink API changed? The status has no Drivetrain.Odometer.",
                "Bluelink API changed? The status has no Green.BatteryManagement.BatteryRemain.Ratio."
            ],
            logger.Warnings.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Green.ChargingInformation.ConnectorFastening.State", 7)]
    [InlineData("Green.ChargingConnector.LockState", 6)]
    [InlineData("Cabin.Door.Row2.Right.Lock", 2)]
    [InlineData("Body.Windshield.Front.Defog.State", 3)]
    [InlineData("Cabin.SteeringWheel.Heat.State", -1)]
    [InlineData("Chassis.Axle.Tire.PressureUnit", 4)]
    [InlineData("Drivetrain.FuelSystem.DTE.Unit", 4)]
    [InlineData("Cabin.HVAC.Row1.Driver.Temperature.Unit", 2)]
    public void WhenCodeIsUnknown_ThenItIsLoggedOnce(string path, int code)
    {
        // Act
        var logger = ParseFixtureTwice(path, code);

        // Assert
        Assert.Equal($"Bluelink API changed? {path} has the unknown value {code}.", Assert.Single(logger.Entries).Message);
    }

    [Theory]
    [InlineData("Green.ChargingConnector.LockState", 5)]
    [InlineData("Body.Windshield.Front.Defog.State", 2)]
    [InlineData("Chassis.Axle.Tire.PressureUnit", 3)]
    [InlineData("Drivetrain.FuelSystem.DTE.Unit", 3)]
    [InlineData("Cabin.HVAC.Row1.Driver.Temperature.Unit", 1)]
    public void WhenCodeIsKnown_ThenNothingIsLogged(string path, int code)
    {
        // Act
        var logger = ParseFixtureTwice(path, code);

        // Assert
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("Green.BatteryManagement.BatteryRemain.Ratio", "abc")]
    [InlineData("Drivetrain.Odometer", "n/a")]
    [InlineData("Drivetrain.FuelSystem.DTE.Total", "--")]
    [InlineData("Cabin.Door.Row1.Driver.Lock", "locked")]
    [InlineData("Date", "yesterday")]
    public void WhenCoreValueHasAnUnexpectedFormat_ThenItsRawTextIsLoggedOnce(string path, string value)
    {
        // Act
        var logger = ParseFixtureTwice(path, value);

        // Assert
        Assert.Equal($"Bluelink API changed? {path} has the unexpected value {value}.", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void WhenTheFormatOfAFieldChangesWithEveryValue_ThenOnlyTheFirstValueIsLogged()
    {
        // Arrange
        var logger = ParseFixtureTwice("Date", "2026-11-22T10:15:00Z");

        // Act
        var secondLogger = ParseFixtureTwice("Date", "2026-11-22T10:20:00Z");

        // Assert
        Assert.Equal("Bluelink API changed? Date has the unexpected value 2026-11-22T10:15:00Z.", Assert.Single(logger.Entries).Message);
        Assert.Empty(secondLogger.Entries);
    }

    [Fact]
    public void WhenUnexpectedValueIsLong_ThenItIsShortenedToFortyCharacters()
    {
        // Act
        var logger = ParseFixtureTwice("Drivetrain.Odometer", new string('x', 50) + " km");

        // Assert
        Assert.Equal($"Bluelink API changed? Drivetrain.Odometer has the unexpected value {new string('x', 40)}.", Assert.Single(logger.Entries).Message);
    }

    [Fact]
    public void WhenWarningsAreDisabled_ThenTheProblemIsLoggedByTheNextLogger()
    {
        // Arrange
        using var document = JsonDocument.Parse("{}");
        BluelinkStatusParser.Parse(document.RootElement, NullLogger.Instance);
        var logger = new RecordingLogger();

        // Act
        BluelinkStatusParser.Parse(document.RootElement, logger);

        // Assert
        Assert.Contains("Bluelink API changed? The status has no Date.", logger.Warnings);
    }

    [Fact]
    public void WhenCoreValueIsAnObject_ThenOnlyItsKindIsLogged()
    {
        // Act
        var logger = ParseFixtureTwice("Drivetrain.Odometer", new JsonObject { ["Value"] = 16773.9 });

        // Assert
        Assert.Equal("Bluelink API changed? Drivetrain.Odometer has the unexpected value Object.", Assert.Single(logger.Entries).Message);
    }
}
