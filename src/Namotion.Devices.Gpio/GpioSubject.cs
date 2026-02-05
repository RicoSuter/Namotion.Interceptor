using System.Collections.Concurrent;
using System.ComponentModel;
using System.Device.Gpio;
using System.Device.I2c;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Iot.Device.Adc;
using Iot.Device.Ads1115;
using Iot.Device.Spi;
using Microsoft.Extensions.Hosting;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Devices.Gpio.Simulation;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a GPIO controller with auto-discovered pins
/// and optional ADC support. Supports Raspberry Pi, Orange Pi, BeagleBone,
/// and other Linux-based boards via System.Device.Gpio.
/// </summary>
[Category("Devices")]
[Description("GPIO pins and analog channels for Raspberry Pi and other Linux boards")]
[InterceptorSubject]
public partial class GpioSubject : BackgroundService, IConfigurableSubject, IHostedSubject, ITitleProvider, IIconProvider
{
    private readonly GpioDriver? _driver;
    private readonly ConcurrentDictionary<int, PinChangeEventHandler> _interruptHandlers = new();
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);

    private GpioController? _controller;
    private bool _currentlyUsingSimulation;
    private Mcp3008? _mcp3008;
    private Ads1115? _ads1115;

    // Track applied ADC configurations to avoid unnecessary recreation
    private Mcp3008Configuration? _appliedMcp3008Config;
    private Ads1115Configuration? _appliedAds1115Config;

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
    /// When true, uses a simulation driver instead of real hardware.
    /// Enables GPIO functionality on non-Pi platforms for testing/development.
    /// </summary>
    [State(IsDiscrete = true)]
    [Configuration]
    public partial bool UseSimulation { get; set; }

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
    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Status message (e.g., "Platform not supported").
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    /// <inheritdoc />
    public string Title => "GPIO";

    /// <inheritdoc />
    public string IconName => "Memory";

    /// <inheritdoc />
    [Derived]
    public string IconColor => Status switch
    {
        ServiceStatus.Running => "Success",
        ServiceStatus.Error => "Error",
        _ => "Warning"
    };

    /// <summary>
    /// Creates a GpioSubject with optional context and GPIO driver.
    /// </summary>
    /// <param name="driver">Optional GPIO driver. If null, uses system default or simulation based on UseSimulation.</param>
    public GpioSubject(GpioDriver? driver = null)
    {
        _driver = driver;

        PollingInterval = TimeSpan.FromSeconds(5);
        RetryInterval = TimeSpan.FromSeconds(30);

        Pins = new Dictionary<int, GpioPin>();
        AnalogChannels = new Dictionary<int, AnalogChannel>();

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Sets friendly names for Raspberry Pi GPIO pins based on their common functions.
    /// </summary>
    [Operation(Title = "Set Raspberry Pi Pin Names")]
    public void SetRaspberryPiPinNames()
    {
        var pinNames = new Dictionary<int, string>
        {
            [2] = "I2C1 SDA",
            [3] = "I2C1 SCL",
            [4] = "GPCLK0",
            [7] = "SPI0 CE1",
            [8] = "SPI0 CE0",
            [9] = "SPI0 MISO",
            [10] = "SPI0 MOSI",
            [11] = "SPI0 SCLK",
            [12] = "PWM0",
            [13] = "PWM1",
            [14] = "UART TX",
            [15] = "UART RX",
            [17] = "GPIO17",
            [18] = "PCM CLK / PWM0",
            [22] = "GPIO22",
            [23] = "GPIO23",
            [24] = "GPIO24",
            [25] = "GPIO25",
            [27] = "GPIO27"
        };

        foreach (var (pinNumber, name) in pinNames)
        {
            if (Pins.TryGetValue(pinNumber, out var pin))
            {
                pin.Name = name;
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var result = TryInitialize();
            if (result == InitializationResult.Success)
            {
                await RunPollingLoopAsync(stoppingToken);
                // If polling loop exits (e.g., due to config change), continue outer loop
                continue;
            }

            if (result == InitializationResult.PermanentFailure)
            {
                // Wait for configuration change (UseSimulation might be enabled)
                try
                {
                    await _configChangedSignal.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            // TransientFailure - retry after delay
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
            if (_driver != null)
            {
                // Use injected driver (for testing)
                _controller = new GpioController(PinNumberingScheme.Logical, _driver);
                _currentlyUsingSimulation = false;
            }
            else if (UseSimulation)
            {
                // Use simulation driver (28 pins like Raspberry Pi)
                _controller = new GpioController(PinNumberingScheme.Logical, new SimulationGpioDriver());
                _currentlyUsingSimulation = true;
            }
            else
            {
                // Use real hardware
                _controller = new GpioController();
                _currentlyUsingSimulation = false;
            }
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

        var existingPins = Pins;
        var pinCount = _controller.PinCount;

        // Build new dictionary, reusing existing pins
        var newPins = new Dictionary<int, GpioPin>(pinCount);
        for (var pinNumber = 0; pinNumber < pinCount; pinNumber++)
        {
            var pin = existingPins.TryGetValue(pinNumber, out var existing)
                ? existing
                : new GpioPin { Mode = GpioPinMode.Input };

            pin.PinNumber = pinNumber;
            InitializePin(pin);
            newPins[pinNumber] = pin;
        }

        // Only reassign if structure changed
        if (existingPins.Count != pinCount || existingPins.Keys.Any(k => !newPins.ContainsKey(k)))
        {
            Pins = newPins;
        }
    }

    private GpioPin CreateAndOpenPin(int pinNumber)
    {
        var pin = new GpioPin
        {
            PinNumber = pinNumber,
            Mode = GpioPinMode.Input
        };

        InitializePin(pin);
        return pin;
    }

    private void InitializePin(GpioPin pin)
    {
        if (_controller == null)
            throw new InvalidOperationException("Controller not initialized");

        pin.Controller = _controller;
        pin.RegisterInterrupt = RegisterInterrupt;
        pin.UnregisterInterrupt = UnregisterInterrupt;

        try
        {
            var pinMode = pin.Mode switch
            {
                GpioPinMode.Input => PinMode.Input,
                GpioPinMode.InputPullUp => PinMode.InputPullUp,
                GpioPinMode.InputPullDown => PinMode.InputPullDown,
                GpioPinMode.Output => PinMode.Output,
                _ => PinMode.Input
            };
            _controller.OpenPin(pin.PinNumber, pinMode);

            pin.Value = _controller.Read(pin.PinNumber) == PinValue.High;
            pin.Status = ServiceStatus.Running;
            pin.StatusMessage = null;

            // Only register interrupt for input pins
            if (pin.Mode != GpioPinMode.Output)
            {
                RegisterInterrupt(pin.PinNumber);
            }
        }
        catch (Exception exception)
        {
            pin.Status = ServiceStatus.Unavailable;
            pin.StatusMessage = exception.Message;
        }
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
        // Handle simulation mode change (only when no injected driver)
        if (_driver == null && UseSimulation != _currentlyUsingSimulation)
        {
            // Signal waiting loop that configuration changed
            if (_configChangedSignal.CurrentCount == 0)
            {
                _configChangedSignal.Release();
            }

            if (_controller != null)
            {
                // Unregister all interrupts
                foreach (var pinNumber in _interruptHandlers.Keys.ToList())
                {
                    UnregisterInterrupt(pinNumber);
                }

                // Dispose old controller
                _controller.Dispose();
                _controller = null;
            }

            // Recreate controller with new driver
            var result = TryCreateController();
            if (result == InitializationResult.Success)
            {
                DiscoverPins();
                Status = ServiceStatus.Running;
                StatusMessage = null;
            }
        }

        // Handle ADC changes only when configuration actually changed (records have value equality)
        var mcp3008Changed = Mcp3008 != _appliedMcp3008Config;
        var ads1115Changed = Ads1115 != _appliedAds1115Config;

        if (mcp3008Changed || ads1115Changed)
        {
            // Only dispose and reinitialize the ADC that changed
            if (mcp3008Changed)
            {
                _mcp3008?.Dispose();
                _mcp3008 = null;
                _appliedMcp3008Config = Mcp3008 is not null ? Mcp3008 with { } : null;
            }

            if (ads1115Changed)
            {
                _ads1115?.Dispose();
                _ads1115 = null;
                _appliedAds1115Config = Ads1115 is not null ? Ads1115 with { } : null;
            }

            AnalogChannels = InitializeAdcChannels();
        }

        return Task.CompletedTask;
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
                    Source = AdcSource.Mcp3008,
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
                channels[8 + i] = new AnalogChannel()
                {
                    Source = AdcSource.Ads1115,
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
        foreach (var channel in AnalogChannels.Values)
        {
            if (channel.Status != ServiceStatus.Running) continue;

            try
            {
                if (channel.Source == AdcSource.Mcp3008 && _mcp3008 != null)
                {
                    var raw = _mcp3008.Read(channel.ChannelNumber);
                    channel.RawValue = raw;
                    channel.Value = raw / 1023.0;
                }
                else if (channel.Source == AdcSource.Ads1115 && _ads1115 != null)
                {
                    var inputMultiplexer = (InputMultiplexer)channel.ChannelNumber;
                    var raw = _ads1115.ReadRaw(inputMultiplexer);
                    channel.RawValue = raw;
                    channel.Value = raw / 32767.0;
                }
            }
            catch (Exception exception)
            {
                channel.Status = ServiceStatus.Error;
                channel.StatusMessage = exception.Message;
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
