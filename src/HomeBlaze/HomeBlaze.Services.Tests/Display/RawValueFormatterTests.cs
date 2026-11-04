using System.Globalization;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services.Display;
using Xunit;

namespace HomeBlaze.Services.Tests.Display;

public class RawValueFormatterTests
{
    private enum Mode
    {
        Heating
    }

    [Fact]
    public void WhenValueIsNull_ThenTextIsEmpty()
    {
        // Act
        var text = RawValueFormatter.Format(null);

        // Assert
        Assert.Equal(string.Empty, text);
    }

    [Theory]
    [InlineData(1234.5, "1234.5")]
    [InlineData(0.25, "0.25")]
    public void WhenCultureUsesAnotherDecimalSeparator_ThenNumberHasADot(double value, string expected)
    {
        // Arrange
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-CH");
        try
        {
            // Act
            var text = RawValueFormatter.Format(value);

            // Assert
            Assert.Equal(expected, text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void WhenValueIsDecimal_ThenTextIsInvariant()
    {
        // Arrange
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            // Act
            var text = RawValueFormatter.Format(21.5m);

            // Assert
            Assert.Equal("21.5", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void WhenValueIsDateTimeOffset_ThenTextIsIso8601()
    {
        // Arrange
        var value = new DateTimeOffset(2026, 10, 12, 14, 30, 0, TimeSpan.FromHours(2));

        // Act
        var text = RawValueFormatter.Format(value);

        // Assert
        Assert.Equal("2026-10-12T14:30:00.0000000+02:00", text);
    }

    [Fact]
    public void WhenValueIsDateTime_ThenTextIsIso8601()
    {
        // Arrange
        var value = new DateTime(2026, 10, 12, 14, 30, 0, DateTimeKind.Utc);

        // Act
        var text = RawValueFormatter.Format(value);

        // Assert
        Assert.Equal("2026-10-12T14:30:00.0000000Z", text);
    }

    [Fact]
    public void WhenValueIsBooleanEnumOrString_ThenTextIsItsName()
    {
        // Act & Assert
        Assert.Equal("True", RawValueFormatter.Format(true));
        Assert.Equal("Heating", RawValueFormatter.Format(Mode.Heating));
        Assert.Equal("text", RawValueFormatter.Format("text"));
    }

    [Fact]
    public void WhenAValueHoldsASecretConfigurationValue_ThenTextIsEmpty()
    {
        // Arrange
        var credentials = new Credentials("admin", "s3cr3t");
        var connection = new Connection("host", credentials);

        // Act
        var credentialsText = RawValueFormatter.Format(credentials);
        var connectionText = RawValueFormatter.Format(connection);

        // Assert
        Assert.Equal(string.Empty, credentialsText);
        Assert.Equal(string.Empty, connectionText);
    }

    [Fact]
    public void WhenAValueHoldsNoSecret_ThenTextIsItsText()
    {
        // Arrange
        var endpoint = new Endpoint("host", 80);

        // Act
        var text = RawValueFormatter.Format(endpoint);

        // Assert
        Assert.Equal(endpoint.ToString(), text);
    }

    private sealed record Credentials(string User, [property: Configuration(IsSecret = true)] string Password);

    private sealed record Connection(string Host, Credentials Credentials);

    private sealed record Endpoint(string Host, int Port);
}
