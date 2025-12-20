using System.Collections.Concurrent;
using System.Device.Gpio;
using System.Device.I2c;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Iot.Device.Adc;
using Iot.Device.Ads1115;
using Iot.Device.Spi;
using Microsoft.Extensions.Hosting;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Devices.Gpio.Interceptors;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a Raspberry Pi GPIO controller with auto-discovered pins
/// and optional ADC support.
/// </summary>
[InterceptorSubject]
public partial class GpioSubject : BackgroundService, IConfigurableSubject, IHostedSubject, ITitleProvider, IIconProvider
{
    private GpioController? _controller;
    private Mcp3008? _mcp3008;
    private Ads1115? _ads1115;
    private readonly ConcurrentDictionary<int, PinChangeEventHandler> _interruptHandlers = new();

    /// <summary>
    /// Auto-discovered GPIO pins indexed by pin number.
    /// </summary>
    [State]
    public partial Dictionary<int, GpioPin> Pins { get; set; }

    /// <summary>
    /// Optional MCP3008 ADC configuration.
    /// </summary>
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }

    /// <summary>
    /// Optional ADS1115 ADC configuration.
    /// </summary>
    [Configuration]
    public partial Ads1115Configuration? Ads1115 { get; set; }

    /// <summary>
    /// Analog channels from configured ADC (empty if no ADC configured).
    /// </summary>
    [State]
    public partial Dictionary<int, AnalogChannel> AnalogChannels { get; set; }

    /// <summary>
    /// Polling interval for GPIO verification and ADC reading.
    /// </summary>
    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    /// <summary>
    /// Current status of the GPIO controller.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Status message (e.g., "Platform not supported").
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <inheritdoc />
    public string? Title => "GPIO";

    /// <inheritdoc />
    public string? Icon => "Memory";

    public GpioSubject()
    {
        Pins = new Dictionary<int, GpioPin>();
        AnalogChannels = new Dictionary<int, AnalogChannel>();
        PollingInterval = TimeSpan.FromSeconds(5);
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Status = ServiceStatus.Starting;
        StatusMessage = "Initializing GPIO controller...";

        // Try to create GpioController
        try
        {
            _controller = new GpioController();
        }
        catch (PlatformNotSupportedException)
        {
            Status = ServiceStatus.Unavailable;
            StatusMessage = "GPIO not supported on this platform";

            // Create all pins with Unavailable status
            // Build dictionary first, then assign to trigger lifecycle attachment
            var pins = new Dictionary<int, GpioPin>();
            for (int pinNumber = 0; pinNumber <= 27; pinNumber++)
            {
                pins[pinNumber] = new GpioPin()
                {
                    PinNumber = pinNumber,
                    Status = ServiceStatus.Unavailable,
                    StatusMessage = "GPIO not supported on this platform"
                };
            }
            Pins = pins;
            return;
        }
        catch (Exception exception)
        {
            Status = ServiceStatus.Error;
            StatusMessage = $"Failed to initialize GPIO: {exception.Message}";
            return;
        }

        // Register interceptors on this subject's context
        var subjectContext = ((IInterceptorSubject)this).Context;
        subjectContext.AddService<IWriteInterceptor>(new GpioWriteInterceptor(_controller));
        subjectContext.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
            _controller,
            RegisterInterrupt,
            UnregisterInterrupt));

        // Discover all BCM pins 0-27
        // Build dictionary first, then assign to trigger lifecycle attachment
        var discoveredPins = new Dictionary<int, GpioPin>();
        for (int pinNumber = 0; pinNumber <= 27; pinNumber++)
        {
            var pin = new GpioPin()
            {
                PinNumber = pinNumber,
                Mode = GpioPinMode.Input
            };

            try
            {
                _controller.OpenPin(pinNumber, PinMode.Input);
                pin.Value = _controller.Read(pinNumber) == PinValue.High;
                pin.Status = ServiceStatus.Running;
                pin.StatusMessage = null;

                // Register interrupt for input pin
                RegisterInterrupt(pinNumber);
            }
            catch (Exception exception)
            {
                pin.Status = ServiceStatus.Unavailable;
                pin.StatusMessage = exception.Message;
            }

            discoveredPins[pinNumber] = pin;
        }
        Pins = discoveredPins;

        Status = ServiceStatus.Running;
        StatusMessage = null;

        // Polling loop for verification and ADC
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Verify/sync all pins
                foreach (var pin in Pins.Values)
                {
                    if (pin.Status != ServiceStatus.Running)
                        continue;

                    var actualValue = _controller.Read(pin.PinNumber) == PinValue.High;
                    if (pin.Value != actualValue)
                    {
                        pin.Value = actualValue;
                    }
                }

                // Poll ADC channels
                PollAdcChannels();

                await Task.Delay(PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Status = ServiceStatus.Error;
                StatusMessage = $"Polling error: {exception.Message}";
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }

        Status = ServiceStatus.Stopping;
        StatusMessage = "Shutting down...";
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        // Dispose existing ADC
        _mcp3008?.Dispose();
        _ads1115?.Dispose();
        _mcp3008 = null;
        _ads1115 = null;

        // Build dictionary first, then assign to trigger lifecycle attachment
        var channels = new Dictionary<int, AnalogChannel>();

        // Initialize MCP3008 if configured
        if (Mcp3008 != null && _controller != null)
        {
            try
            {
                var spi = new SoftwareSpi(
                    Mcp3008.ClockPin,
                    Mcp3008.MisoPin,
                    Mcp3008.MosiPin,
                    Mcp3008.ChipSelectPin);
                _mcp3008 = new Mcp3008(spi);

                for (int i = 0; i < 8; i++)
                {
                    channels[i] = new AnalogChannel()
                    {
                        ChannelNumber = i,
                        Status = ServiceStatus.Running
                    };
                }
            }
            catch (Exception exception)
            {
                StatusMessage = $"MCP3008 init failed: {exception.Message}";
            }
        }

        // Initialize ADS1115 if configured
        if (Ads1115 != null)
        {
            try
            {
                var i2cDevice = I2cDevice.Create(new I2cConnectionSettings(
                    Ads1115.I2cBus,
                    Ads1115.Address));
                _ads1115 = new Ads1115(i2cDevice);

                for (int i = 0; i < 4; i++)
                {
                    channels[i] = new AnalogChannel()
                    {
                        ChannelNumber = i,
                        Status = ServiceStatus.Running
                    };
                }
            }
            catch (Exception exception)
            {
                StatusMessage = $"ADS1115 init failed: {exception.Message}";
            }
        }

        AnalogChannels = channels;
        return Task.CompletedTask;
    }

    private void RegisterInterrupt(int pinNumber)
    {
        if (_controller == null) return;

        PinChangeEventHandler handler = (sender, arguments) =>
        {
            if (Pins.TryGetValue(arguments.PinNumber, out var pin) && pin.Status == ServiceStatus.Running)
            {
                pin.Value = arguments.ChangeType == PinEventTypes.Rising;
            }
        };

        _interruptHandlers[pinNumber] = handler;
        _controller.RegisterCallbackForPinValueChangedEvent(
            pinNumber,
            PinEventTypes.Rising | PinEventTypes.Falling,
            handler);
    }

    private void UnregisterInterrupt(int pinNumber)
    {
        if (_controller == null) return;

        if (_interruptHandlers.TryRemove(pinNumber, out var handler))
        {
            _controller.UnregisterCallbackForPinValueChangedEvent(pinNumber, handler);
        }
    }

    private void PollAdcChannels()
    {
        if (_mcp3008 != null)
        {
            foreach (var channel in AnalogChannels.Values)
            {
                if (channel.Status != ServiceStatus.Running) continue;

                try
                {
                    var raw = _mcp3008.Read(channel.ChannelNumber);
                    channel.RawValue = raw;
                    channel.Value = raw / 1023.0;
                }
                catch (Exception exception)
                {
                    channel.Status = ServiceStatus.Error;
                    channel.StatusMessage = exception.Message;
                }
            }
        }

        if (_ads1115 != null)
        {
            foreach (var channel in AnalogChannels.Values)
            {
                if (channel.Status != ServiceStatus.Running) continue;

                try
                {
                    var inputMultiplexer = (InputMultiplexer)channel.ChannelNumber;
                    var raw = _ads1115.ReadRaw(inputMultiplexer);
                    channel.RawValue = raw;
                    channel.Value = raw / 32767.0;
                }
                catch (Exception exception)
                {
                    channel.Status = ServiceStatus.Error;
                    channel.StatusMessage = exception.Message;
                }
            }
        }
    }

    public override void Dispose()
    {
        Status = ServiceStatus.Stopped;
        StatusMessage = null;

        // Unregister all interrupts
        if (_controller != null)
        {
            foreach (var pinNumber in _interruptHandlers.Keys.ToList())
            {
                UnregisterInterrupt(pinNumber);
            }
        }

        // Dispose hardware
        _mcp3008?.Dispose();
        _ads1115?.Dispose();
        _controller?.Dispose();

        _mcp3008 = null;
        _ads1115 = null;
        _controller = null;

        base.Dispose();
    }
}
