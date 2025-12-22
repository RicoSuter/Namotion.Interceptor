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
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a GPIO controller with auto-discovered pins
/// and optional ADC support. Supports Raspberry Pi, Orange Pi, BeagleBone,
/// and other Linux-based boards via System.Device.Gpio.
/// </summary>
[InterceptorSubject]
public partial class GpioSubject : BackgroundService, IConfigurableSubject, IHostedSubject, ITitleProvider, IIconProvider
{
    private readonly GpioDriver? _driver;
    private readonly ConcurrentDictionary<int, PinChangeEventHandler> _interruptHandlers = new();

    private GpioController? _controller;
    private Mcp3008? _mcp3008;
    private Ads1115? _ads1115;

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
    /// Polling interval for GPIO verification and ADC reading.
    /// </summary>
    [Configuration]
    public partial TimeSpan PollingInterval { get; set; }

    /// <summary>
    /// Retry interval when initialization fails.
    /// </summary>
    [Configuration]
    public partial TimeSpan RetryInterval { get; set; }

    /// <summary>
    /// Auto-discovered GPIO pins indexed by pin number.
    /// </summary>
    [State]
    [Configuration]
    public partial Dictionary<int, GpioPin> Pins { get; set; }

    /// <summary>
    /// Analog channels from configured ADC (empty if no ADC configured).
    /// </summary>
    [State]
    public partial Dictionary<int, AnalogChannel> AnalogChannels { get; set; }

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
    public string Title => "GPIO";

    /// <inheritdoc />
    public string Icon => "Memory";

    public GpioSubject()
    {
        PollingInterval = TimeSpan.FromSeconds(5);
        RetryInterval = TimeSpan.FromSeconds(30);

        Pins = new Dictionary<int, GpioPin>();
        AnalogChannels = new Dictionary<int, AnalogChannel>();

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Creates a GpioSubject with a custom GPIO driver.
    /// Use this constructor for code-based usage with custom drivers or for testing.
    /// </summary>
    /// <param name="driver">The GPIO driver to use.</param>
    public GpioSubject(GpioDriver driver) : this()
    {
        _driver = driver;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Retry loop for initialization
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = TryInitialize();
            if (result == InitializationResult.Success)
            {
                await RunPollingLoopAsync(stoppingToken);
                break;
            }

            if (result == InitializationResult.PermanentFailure)
            {
                // Don't retry for permanent failures (e.g., platform not supported)
                return;
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(RetryInterval, stoppingToken);
            }
        }

        Status = ServiceStatus.Stopping;
        StatusMessage = "Shutting down...";
    }

    private enum InitializationResult
    {
        Success,
        TransientFailure,
        PermanentFailure
    }

    private InitializationResult TryInitialize()
    {
        Status = ServiceStatus.Starting;
        StatusMessage = "Initializing GPIO controller...";

        var controllerResult = TryCreateController();
        if (controllerResult != InitializationResult.Success)
        {
            return controllerResult;
        }

        DiscoverPins();
        Status = ServiceStatus.Running;
        StatusMessage = null;

        return InitializationResult.Success;
    }

    private InitializationResult TryCreateController()
    {
        try
        {
            _controller = _driver != null 
                ? new GpioController(PinNumberingScheme.Logical, _driver) 
                : new GpioController();
            return InitializationResult.Success;
        }
        catch (PlatformNotSupportedException)
        {
            CreateUnavailablePins("GPIO not supported on this platform");
            return InitializationResult.PermanentFailure;
        }
        catch (Exception exception)
        {
            Status = ServiceStatus.Error;
            StatusMessage = $"Failed to initialize GPIO: {exception.Message}";
            return InitializationResult.TransientFailure;
        }
    }

    private void CreateUnavailablePins(string message)
    {
        Status = ServiceStatus.Unavailable;
        StatusMessage = message;
        Pins = new Dictionary<int, GpioPin>();
    }

    private void DiscoverPins()
    {
        if (_controller == null) return;

        var discoveredPins = new Dictionary<int, GpioPin>();
        var pinCount = _controller.PinCount;
        
        for (var pinNumber = 0; pinNumber < pinCount; pinNumber++)
        {
            var pin = CreateAndOpenPin(pinNumber);
            discoveredPins[pinNumber] = pin;
        }

        Pins = discoveredPins;
    }

    private GpioPin CreateAndOpenPin(int pinNumber)
    {
        if (_controller == null)
            throw new InvalidOperationException("Controller not initialized");

        var pin = new GpioPin()
        {
            PinNumber = pinNumber,
            Mode = GpioPinMode.Input,
            Controller = _controller,
            RegisterInterrupt = RegisterInterrupt,
            UnregisterInterrupt = UnregisterInterrupt
        };

        try
        {
            _controller.OpenPin(pinNumber, PinMode.Input);
            pin.Value = _controller.Read(pinNumber) == PinValue.High;
            pin.Status = ServiceStatus.Running;
            pin.StatusMessage = null;
            RegisterInterrupt(pinNumber);
        }
        catch (Exception exception)
        {
            pin.Status = ServiceStatus.Unavailable;
            pin.StatusMessage = exception.Message;
        }

        return pin;
    }

    private async Task RunPollingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                VerifyPins();
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
    }

    private void VerifyPins()
    {
        if (_controller == null) return;

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
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        DisposeAdcDevices();
        var channels = InitializeAdcChannels();
        AnalogChannels = channels;
        return Task.CompletedTask;
    }

    private void DisposeAdcDevices()
    {
        _mcp3008?.Dispose();
        _ads1115?.Dispose();
        _mcp3008 = null;
        _ads1115 = null;
    }

    private Dictionary<int, AnalogChannel> InitializeAdcChannels()
    {
        var channels = new Dictionary<int, AnalogChannel>();

        if (Mcp3008 != null)
        {
            InitializeMcp3008(channels);
        }

        if (Ads1115 != null)
        {
            InitializeAds1115(channels);
        }

        return channels;
    }

    private void InitializeMcp3008(Dictionary<int, AnalogChannel> channels)
    {
        if (_controller == null) return;

        try
        {
            var spi = new SoftwareSpi(
                Mcp3008!.ClockPin,
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

    private void InitializeAds1115(Dictionary<int, AnalogChannel> channels)
    {
        try
        {
            var i2CDevice = I2cDevice.Create(new I2cConnectionSettings(
                Ads1115!.I2cBus,
                Ads1115.Address));

            _ads1115 = new Ads1115(i2CDevice);
            for (var i = 0; i < 4; i++)
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

    private void RegisterInterrupt(int pinNumber)
    {
        if (_controller == null) return;

        PinChangeEventHandler handler = (_, arguments) =>
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
