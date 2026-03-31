namespace Namotion.Devices.Gpio.Configuration;

/// <summary>
/// Configuration for MCP3008 10-bit SPI ADC.
/// </summary>
public record Mcp3008Configuration
{
    /// <summary>
    /// SPI clock pin (BCM numbering).
    /// </summary>
    public int ClockPin { get; set; } = 11;

    /// <summary>
    /// SPI MOSI pin (BCM numbering).
    /// </summary>
    public int MosiPin { get; set; } = 10;

    /// <summary>
    /// SPI MISO pin (BCM numbering).
    /// </summary>
    public int MisoPin { get; set; } = 9;

    /// <summary>
    /// SPI chip select pin (BCM numbering).
    /// </summary>
    public int ChipSelectPin { get; set; } = 8;
}
