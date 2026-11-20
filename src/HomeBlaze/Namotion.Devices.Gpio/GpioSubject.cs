using System.Collections.Concurrent;
using System.ComponentModel;
using System.Device.Gpio;
using System.Device.I2c;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using Iot.Device.Adc;
using Iot.Device.Ads1115;
using Iot.Device.Spi;
using Microsoft.Extensions.Hosting;
using Namotion.Devices.Gpio.Configuration;
using Namotion.Devices.Gpio.Simulation;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a GPIO controller with auto-discovered pins
/// and optional ADC support. Supports Raspberry Pi, Orange Pi, BeagleBone,
/// and other Linux-based boards via System.Device.Gpio.
/// </summary>
[Category("Hardware")]
[Description("GPIO pins and analog channels for Raspberry Pi and other Linux boards")]
[DisplayName("GPIO")]
[SubjectIcon("Memory")]
[InterceptorSubject]
public partial class GpioSubject : BackgroundService, IConfigurable, IMonitoredService, ITitleProvider, IIconProvider
{
    private readonly GpioDriver? _driver;
    private readonly ConcurrentDictionary<int, PinChangeEventHandler> _interruptHandlers = new();
    private readonly SemaphoreSlim _configChangedSignal = new(0, 1);

    // Serializes creating and disposing the controller and attaching pins to it, which the service loop and an
    // applied configuration both do. Never taken under a pin lock: attaching a pin takes the pin lock under this one.
    private readonly Lock _controllerLock = new();

    private const int MaximumDiscoveryAttempts = 3;

    private static readonly Dictionary<int, string> RaspberryPiPinNames = new()
    {
        [0] = "ID_SD (HAT EEPROM)",
        [1] = "ID_SC (HAT EEPROM)",
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
        [18] = "PCM CLK / PWM0"
    };

    private GpioController? _controller;

    // Guarded by _controllerLock. The pins that hold the controller, by pin number. A reload of the configuration
    // replaces the instances in Pins, so these are the ones to detach.
    private Dictionary<int, GpioPin> _attachedPins = new();

    // Guarded by _controllerLock. The stopping token is canceled only after Dispose released the lock, so the service
    // loop and an applied configuration check this to create nothing after Dispose.
    private bool _isDisposed;
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
    [State(IsDiscrete = true, Position = 400)]
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
    public partial Dictionary<int, AnalogChannel> AnalogChannels { get; internal set; }

    /// <summary>
    /// Current status of the GPIO controller.
    /// </summary>
    [State(IsDiscrete = true)]
    public partial ServiceStatus Status { get; internal set; }

    /// <summary>
    /// Status message (e.g., "Platform not supported").
    /// </summary>
    [State]
    public partial string? StatusMessage { get; internal set; }

    /// <inheritdoc />
    public string Title => "GPIO";

    /// <inheritdoc />
    public string IconName => "Memory";

    /// <inheritdoc />
    [Derived]
    public string? IconColor => Status == ServiceStatus.Running ? "Success" : null;

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
    /// Applies the defaults of a Raspberry Pi 3, 4 or Zero 2 W to the unconfigured pins, those without a name in plain
    /// <see cref="GpioPinMode.Input"/> mode: the name of a pin with a fixed function, and the pull the Pi applies at
    /// power-on, up for GPIO 0 to 8 and down for GPIO 9 to 27. Other pins and the pins of a configured
    /// <see cref="Mcp3008"/> are not changed, so applying it again changes nothing. Saves the configuration when a pin
    /// changed, also when a later pin failed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The mode of a pin could not be changed, or the configuration writer stored nothing.</exception>
    [Operation(Title = "Apply Raspberry Pi Defaults", Description = "Names the pins with a fixed function and applies the power-on pulls of a Raspberry Pi 3, 4 or Zero 2 W to unconfigured pins. Check the defaults before using it on a Raspberry Pi 5.")]
    public async Task ApplyRaspberryPiDefaultsAsync(CancellationToken cancellationToken)
    {
        var mcp3008 = Mcp3008;
        var isChanged = false;
        var isCompleted = false;
        try
        {
            foreach (var (pinNumber, pin) in Pins)
            {
                if (!string.IsNullOrEmpty(pin.Name) || pin.Mode != GpioPinMode.Input ||
                    (mcp3008 is not null && IsMcp3008Pin(mcp3008, pinNumber)))
                {
                    continue;
                }

                if (pinNumber <= 27)
                {
                    pin.Mode = pinNumber <= 8 ? GpioPinMode.InputPullUp : GpioPinMode.InputPullDown;
                    isChanged = true;
                }

                if (RaspberryPiPinNames.TryGetValue(pinNumber, out var name))
                {
                    pin.Name = name;
                    isChanged = true;
                }
            }

            isCompleted = true;
        }
        finally
        {
            if (isChanged)
            {
                await SaveConfigurationAsync(isLoopCompleted: isCompleted, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Writes the configuration through the configuration writer of a parent, if any. After a failed loop it never
    /// throws, so that the exception of the loop propagates from the finally block.
    /// </summary>
    private async Task SaveConfigurationAsync(bool isLoopCompleted, CancellationToken cancellationToken)
    {
        var configurationWriter = this.TryGetFirstParent<IConfigurationWriter>();
        if (configurationWriter is null)
        {
            return;
        }

        if (!isLoopCompleted)
        {
            try
            {
                await configurationWriter.WriteConfigurationAsync(this, cancellationToken);
            }
            catch (Exception)
            {
                // The exception of the loop is the one reported.
            }

            return;
        }

        if (!await configurationWriter.WriteConfigurationAsync(this, cancellationToken))
        {
            throw new InvalidOperationException("The Raspberry Pi defaults were applied but the configuration could not be saved.");
        }
    }

    private static bool IsMcp3008Pin(Mcp3008Configuration configuration, int pinNumber) =>
        pinNumber == configuration.ClockPin || pinNumber == configuration.MosiPin ||
        pinNumber == configuration.MisoPin || pinNumber == configuration.ChipSelectPin;

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
        lock (_controllerLock)
        {
            if (_isDisposed)
            {
                return InitializationResult.PermanentFailure;
            }

            Status = ServiceStatus.Starting;
            StatusMessage = "Initializing GPIO controller...";

            // An applied configuration that enabled the simulation has created the controller already.
            if (_controller is null)
            {
                var controllerResult = TryCreateController();
                if (controllerResult != InitializationResult.Success)
                {
                    return controllerResult;
                }
            }

            DiscoverPins();
            Status = ServiceStatus.Running;
            StatusMessage = null;

            return InitializationResult.Success;
        }
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

    /// <summary>
    /// Fills <see cref="Pins"/> with a pin for every pin of the controller and attaches the pins that are not attached
    /// yet, after detaching the instances they replaced. Without a controller, only the pin numbers are set. Called
    /// under <see cref="_controllerLock"/>.
    /// </summary>
    private void DiscoverPins()
    {
        if (_controller == null)
        {
            foreach (var (pinNumber, pin) in Pins)
            {
                pin.PinNumber = pinNumber;
            }

            return;
        }

        var pinCount = _controller.PinCount;

        // A reloaded configuration replaces Pins without this lock. It is followed by an apply, which waits for this
        // lock, so the replaced dictionary is attached again rather than overwritten; the attempts only bound the loop.
        for (var attempt = 0; attempt < MaximumDiscoveryAttempts; attempt++)
        {
            var existingPins = Pins;
            var newPins = new Dictionary<int, GpioPin>(pinCount);
            for (var pinNumber = 0; pinNumber < pinCount; pinNumber++)
            {
                var pin = existingPins.TryGetValue(pinNumber, out var existing)
                    ? existing
                    : new GpioPin { Mode = GpioPinMode.Input };

                _attachedPins.TryGetValue(pinNumber, out var attachedPin);
                if (!ReferenceEquals(pin, attachedPin))
                {
                    if (attachedPin is not null)
                    {
                        // Detached first, so that a mode change on the replaced instance cannot register an interrupt
                        // handler after this pin number's handler was unregistered.
                        attachedPin.Detach();
                        UnregisterInterrupt(pinNumber);
                    }

                    pin.PinNumber = pinNumber;
                    InitializePin(pin);
                }

                newPins[pinNumber] = pin;
            }

            _attachedPins = newPins;

            var isStructureChanged = existingPins.Count != pinCount || existingPins.Keys.Any(key => !newPins.ContainsKey(key));
            if (!isStructureChanged)
            {
                return;
            }

            if (ReferenceEquals(Pins, existingPins))
            {
                Pins = newPins;
                return;
            }
        }
    }

    private void InitializePin(GpioPin pin)
    {
        if (_controller == null)
            throw new InvalidOperationException("Controller not initialized");

        pin.CancelDebounce();
        pin.Controller = _controller;
        pin.RegisterInterrupt = RegisterInterrupt;
        pin.UnregisterInterrupt = UnregisterInterrupt;

        try
        {
            if (pin.Mode == GpioPinMode.Output)
            {
                // The level is read before the mode is set, so an output keeps the level it has: the latch after a
                // restart, the power-on pull after a reboot. This relies on opening without a mode not changing the
                // pin. A driver that cannot read a pin before its mode is set (libgpiod 1.x) opens it as an output its
                // own way, and the level is read back from it.
                _controller.OpenPin(pin.PinNumber);
                PinValue level;
                if (TryReadLevel(_controller, pin.PinNumber, out var levelBeforeMode))
                {
                    level = levelBeforeMode;
                    _controller.OpenPin(pin.PinNumber, PinMode.Output, level);
                }
                else
                {
                    _controller.OpenPin(pin.PinNumber, PinMode.Output);
                    level = _controller.Read(pin.PinNumber);
                }

                pin.Value = pin.ToValue(level);
            }
            else
            {
                var pinMode = pin.Mode switch
                {
                    GpioPinMode.InputPullUp => PinMode.InputPullUp,
                    GpioPinMode.InputPullDown => PinMode.InputPullDown,
                    _ => PinMode.Input
                };

                _controller.OpenPin(pin.PinNumber, pinMode);
                pin.Value = pin.ToValue(_controller.Read(pin.PinNumber));

                // Registered before the pin reports Running, so that an edge after that has a handler.
                RegisterInterrupt(pin.PinNumber);
            }

            pin.Status = ServiceStatus.Running;
            pin.StatusMessage = null;
        }
        catch (Exception exception)
        {
            pin.Status = ServiceStatus.Unavailable;
            pin.StatusMessage = exception.Message;
        }
    }

    private static bool TryReadLevel(GpioController controller, int pinNumber, out PinValue level)
    {
        try
        {
            level = controller.Read(pinNumber);
            return true;
        }
        catch (IOException)
        {
            // The libgpiod V1 driver reads a pin only once its mode is set.
            level = default;
            return false;
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
        foreach (var pin in Pins.Values)
        {
            pin.PollHardwareLevel();
        }
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
    {
        lock (_controllerLock)
        {
            if (_isDisposed)
            {
                return Task.CompletedTask;
            }

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
                    DetachAndUnregisterPins();

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
            else
            {
                // A reloaded configuration has replaced the pins with new instances that hold neither a pin number
                // nor the controller.
                DiscoverPins();
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
            if (!Pins.TryGetValue(arguments.PinNumber, out var pin))
                return;

            try
            {
                pin.ApplyHardwareLevel(arguments.ChangeType == PinEventTypes.Rising);
            }
            catch (Exception exception)
            {
                // Runs on the driver's thread, where a thrown exception would end the process.
                pin.Status = ServiceStatus.Error;
                pin.StatusMessage = $"Failed to apply an edge of GPIO pin {pin.PinNumber}: {exception.Message}";
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

    // Called under _controllerLock. The pins are detached before the interrupts are unregistered, so that a mode change
    // cannot register a handler that is never unregistered.
    private void DetachAndUnregisterPins()
    {
        foreach (var pin in _attachedPins.Values)
        {
            pin.Detach();
        }

        _attachedPins = new Dictionary<int, GpioPin>();

        foreach (var pinNumber in _interruptHandlers.Keys.ToList())
        {
            UnregisterInterrupt(pinNumber);
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
        lock (_controllerLock)
        {
            _isDisposed = true;
            Status = ServiceStatus.Stopped;
            StatusMessage = null;

            if (_controller != null)
            {
                DetachAndUnregisterPins();
            }

            // Dispose hardware
            _mcp3008?.Dispose();
            _ads1115?.Dispose();
            _controller?.Dispose();

            _mcp3008 = null;
            _ads1115 = null;
            _controller = null;
        }

        base.Dispose();
    }
}
