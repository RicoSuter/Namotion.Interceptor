namespace Namotion.Devices.Gpio.Configuration;

/// <summary>
/// Configuration for ADS1115 16-bit I2C ADC.
/// </summary>
public class Ads1115Configuration
{
    /// <summary>
    /// I2C bus number.
    /// </summary>
    public int I2cBus { get; set; } = 1;

    /// <summary>
    /// I2C device address.
    /// </summary>
    public int Address { get; set; } = 0x48;
}
