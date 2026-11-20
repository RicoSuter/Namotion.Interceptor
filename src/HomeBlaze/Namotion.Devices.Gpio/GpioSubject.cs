using System.Collections.Concurrent;
using System.ComponentModel;
using System.Device.Gpio;
using System.Device.Gpio.Drivers;
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
    private readonly bool _isDriverRaspberryPi;
    private readonly Func<Mcp3008Configuration, Mcp3008> _createMcp3008;
    private readonly Func<Ads1115Configuration, Ads1115> _createAds1115;
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

    // Guarded by _controllerLock. The pins that hold the controller, by pin number, updated pin by pin so that an
    // exception leaves it accurate. A reload of the configuration replaces the instances in Pins, so these are the ones
    // to detach.
    private Dictionary<int, GpioPin> _attachedPins = new();

    // Guarded by _controllerLock. The stopping token is canceled only after Dispose released the lock, so the service
    // loop and an applied configuration check this to create nothing after Dispose.
    private bool _isDisposed;
    private bool _currentlyUsingSimulation;

    // Guarded by _controllerLock. Whether the controller drives the pins of a board that uses the Raspberry Pi register
    // driver, whose power-on pulls new pins start with.
    private bool _isRaspberryPi;

    // Guarded by _controllerLock. The configuration an ADC was created with is set only together with the ADC and
    // cleared when it is disposed, so that a configured ADC that does not exist is created by the next apply or start.
    private Mcp3008? _mcp3008;
    private Ads1115? _ads1115;
    private Mcp3008Configuration? _appliedMcp3008Config;
    private Ads1115Configuration? _appliedAds1115Config;

    /// <summary>
    /// Optional MCP3008 ADC configuration. Its pins are not opened as GPIO pins: they show
    /// <see cref="ServiceStatus.Unavailable"/> with the message "Used by the MCP3008".
    /// </summary>
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }

    /// <summary>
    /// Optional ADS1115 ADC configuration. On I2C bus 1, GPIO 2 (SDA) and 3 (SCL) are not opened as GPIO pins: they
    /// show <see cref="ServiceStatus.Unavailable"/> with the message "Used by I2C (ADS1115)".
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
        : this(driver, driver is RaspberryPi3Driver)
    {
    }

    /// <summary>
    /// Creates a GpioSubject with an optional GPIO driver that drives the pins of a board that uses the Raspberry Pi
    /// register driver (Raspberry Pi 3, 4, 400, Zero 2 W and Compute Module 3 and 4) when
    /// <paramref name="isRaspberryPi"/> is true, so that new pins start with its power-on pull. The ADCs are created
    /// with <paramref name="createMcp3008"/> and <paramref name="createAds1115"/>, by default on the hardware.
    /// </summary>
    internal GpioSubject(
        GpioDriver? driver,
        bool isRaspberryPi,
        Func<Mcp3008Configuration, Mcp3008>? createMcp3008 = null,
        Func<Ads1115Configuration, Ads1115>? createAds1115 = null)
    {
        _driver = driver;
        _isDriverRaspberryPi = isRaspberryPi;
        _createMcp3008 = createMcp3008 ?? CreateMcp3008;
        _createAds1115 = createAds1115 ?? CreateAds1115;

        PollingInterval = TimeSpan.FromSeconds(5);
        RetryInterval = TimeSpan.FromSeconds(30);

        Pins = new Dictionary<int, GpioPin>();
        AnalogChannels = new Dictionary<int, AnalogChannel>();

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Applies the defaults of a Raspberry Pi 3, 4, 400, Zero 2 W or Compute Module 3 or 4 to the unconfigured pins,
    /// those without a name in plain <see cref="GpioPinMode.Input"/> mode or in the mode of their power-on pull: the
    /// name of a pin with a fixed function, and the pull the Pi applies at power-on, up for GPIO 0 to 8 and down for
    /// GPIO 9 to 27. Other pins and the pins of a configured <see cref="Mcp3008"/> are not changed, so applying it again
    /// changes nothing. Saves the configuration when a pin changed, also when a later pin failed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The mode of a pin could not be changed, or the configuration writer stored nothing.</exception>
    [Operation(Title = "Apply Raspberry Pi Defaults", Description = "Names the pins with a fixed function and applies the power-on pulls of a Raspberry Pi 3, 4, 400, Zero 2 W or Compute Module 3 or 4 to unconfigured pins. Check the defaults before using it on a Raspberry Pi 5.")]
    public async Task ApplyRaspberryPiDefaultsAsync(CancellationToken cancellationToken)
    {
        var mcp3008 = Mcp3008;
        var isChanged = false;
        var isCompleted = false;
        try
        {
            foreach (var (pinNumber, pin) in Pins)
            {
                // A pin discovered on a Raspberry Pi starts in the mode of its power-on pull, so that mode without a
                // name counts as unconfigured too.
                var powerOnMode = GetRaspberryPiPowerOnMode(pinNumber);
                if (!string.IsNullOrEmpty(pin.Name) ||
                    (pin.Mode != GpioPinMode.Input && pin.Mode != powerOnMode) ||
                    (mcp3008 is not null && IsMcp3008Pin(mcp3008, pinNumber)))
                {
                    continue;
                }

                if (pin.Mode != powerOnMode)
                {
                    pin.Mode = powerOnMode;
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

    /// <summary>
    /// Gets the mode with the pull a Raspberry Pi 3, 4, 400, Zero 2 W or Compute Module 3 or 4 applies at power-on: up
    /// for GPIO 0 to 8, down for GPIO 9 to 27, and plain <see cref="GpioPinMode.Input"/> above.
    /// </summary>
    private static GpioPinMode GetRaspberryPiPowerOnMode(int pinNumber) => pinNumber switch
    {
        <= 8 => GpioPinMode.InputPullUp,
        <= 27 => GpioPinMode.InputPullDown,
        _ => GpioPinMode.Input
    };

    private static bool IsMcp3008Pin(Mcp3008Configuration configuration, int pinNumber) =>
        pinNumber == configuration.ClockPin || pinNumber == configuration.MosiPin ||
        pinNumber == configuration.MisoPin || pinNumber == configuration.ChipSelectPin;

    /// <summary>
    /// Gets why a pin is reserved for a configured ADC, or null when it is not: the pins of an MCP3008, and GPIO 2 (SDA)
    /// and 3 (SCL) for an ADS1115 on I2C bus 1.
    /// </summary>
    private static string? GetReservedPinMessage(int pinNumber, Mcp3008Configuration? mcp3008, Ads1115Configuration? ads1115)
    {
        if (mcp3008 is not null && IsMcp3008Pin(mcp3008, pinNumber))
        {
            return "Used by the MCP3008";
        }

        if (ads1115 is { I2cBus: 1 } && pinNumber is 2 or 3)
        {
            return "Used by I2C (ADS1115)";
        }

        return null;
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
        lock (_controllerLock)
        {
            if (_isDisposed)
            {
                return InitializationResult.PermanentFailure;
            }

            StatusMessage = "Initializing GPIO controller...";
            Status = ServiceStatus.Starting;

            // An applied configuration that enabled the simulation has created the controller already.
            var controllerResult = _controller is null ? TryCreateController() : InitializationResult.Success;

            // Also without a controller, which an ADS1115 does not need.
            var adcFailureMessage = SynchronizeAdcs(Mcp3008, Ads1115);
            if (controllerResult != InitializationResult.Success)
            {
                StatusMessage = CombineMessages(StatusMessage, adcFailureMessage);
                return controllerResult;
            }

            // Set before the status, so that Running is published with its message.
            StatusMessage = adcFailureMessage;
            Status = ServiceStatus.Running;

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
                _isRaspberryPi = _isDriverRaspberryPi;
            }
            else if (UseSimulation)
            {
                // Use simulation driver (28 pins like Raspberry Pi)
                _controller = new GpioController(PinNumberingScheme.Logical, new SimulationGpioDriver());
                _currentlyUsingSimulation = true;
                _isRaspberryPi = false;
            }
            else
            {
                // Use real hardware
                var raspberryPiDriver = TryCreateRaspberryPiDriver();
                _controller = raspberryPiDriver is not null
                    ? new GpioController(PinNumberingScheme.Logical, raspberryPiDriver)
                    : new GpioController();
                _currentlyUsingSimulation = false;
                _isRaspberryPi = raspberryPiDriver is not null;
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

    /// <summary>
    /// Creates the Raspberry Pi register driver on a Raspberry Pi 3, 4, 400, Zero 2 W or Compute Module 3 or 4, or
    /// returns null on other boards.
    /// </summary>
    private static RaspberryPi3Driver? TryCreateRaspberryPiDriver()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        // In System.Device.Gpio 3.2.0, the default controller picks this driver on exactly the boards where its
        // constructor succeeds, so creating it here changes no driver. Check this again when upgrading the package.
        try
        {
            return new RaspberryPi3Driver();
        }
        catch (PlatformNotSupportedException)
        {
            return null;
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
    /// yet, after detaching the instances they replaced. A new pin starts in plain <see cref="GpioPinMode.Input"/>
    /// mode, on a board that uses the Raspberry Pi register driver in the mode of its power-on pull. A pin reserved for
    /// a configured ADC is detached and closed instead, and shows <see cref="ServiceStatus.Unavailable"/>. Without a
    /// controller, only the pin numbers are set. Called under <see cref="_controllerLock"/>.
    /// </summary>
    private void DiscoverPins(Mcp3008Configuration? mcp3008, Ads1115Configuration? ads1115)
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

        // A reloaded configuration replaces Pins without this lock and is followed by an apply, which waits for this
        // lock and attaches the replaced pins. A replacement between the check and the assignment below is
        // overwritten; the attempts only bound the loop.
        for (var attempt = 0; attempt < MaximumDiscoveryAttempts; attempt++)
        {
            var existingPins = Pins;
            var newPins = new Dictionary<int, GpioPin>(pinCount);
            for (var pinNumber = 0; pinNumber < pinCount; pinNumber++)
            {
                var pin = existingPins.TryGetValue(pinNumber, out var existing)
                    ? existing
                    : new GpioPin { Mode = _isRaspberryPi ? GetRaspberryPiPowerOnMode(pinNumber) : GpioPinMode.Input };

                _attachedPins.TryGetValue(pinNumber, out var attachedPin);
                var reservedMessage = GetReservedPinMessage(pinNumber, mcp3008, ads1115);
                if (reservedMessage is not null)
                {
                    if (attachedPin is not null)
                    {
                        DetachPin(pinNumber, attachedPin);
                    }

                    ReservePin(pinNumber, pin, reservedMessage);
                }
                else if (!ReferenceEquals(pin, attachedPin))
                {
                    if (attachedPin is not null)
                    {
                        DetachPin(pinNumber, attachedPin);
                    }

                    _attachedPins[pinNumber] = pin;
                    pin.PinNumber = pinNumber;
                    InitializePin(pin);
                }

                newPins[pinNumber] = pin;
            }

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

    // Called under _controllerLock. Detached before the interrupt is unregistered, so that a mode change on the pin
    // cannot register an interrupt handler after this pin number's handler was unregistered.
    private void DetachPin(int pinNumber, GpioPin pin)
    {
        pin.Detach();
        _attachedPins.Remove(pinNumber);
        UnregisterInterrupt(pinNumber);
    }

    // Called under _controllerLock, before the ADC that uses the pin is created.
    private void ReservePin(int pinNumber, GpioPin pin, string message)
    {
        pin.PinNumber = pinNumber;
        pin.Status = ServiceStatus.Unavailable;
        try
        {
            // Closed now, so that disposing this controller later does not close a pin that the ADC uses by then.
            if (_controller!.IsPinOpen(pinNumber))
            {
                _controller.ClosePin(pinNumber);
            }

            pin.StatusMessage = message;
        }
        catch (Exception exception)
        {
            pin.StatusMessage = $"{message}, but the pin could not be closed: {exception.Message}";
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
                if (TryCreateController() == InitializationResult.Success)
                {
                    Status = ServiceStatus.Running;
                    StatusMessage = null;
                }
            }

            // A reloaded configuration has replaced the pins with new instances that hold neither a pin number nor the
            // controller, so the pins are discovered also when no ADC changed.
            var adcFailureMessage = SynchronizeAdcs(Mcp3008, Ads1115);

            // While running, the status message reports only an ADC that could not be created.
            if (Status == ServiceStatus.Running)
            {
                StatusMessage = adcFailureMessage;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes each ADC whose configuration differs from the given one, discovers the pins with the given
    /// configurations and then creates each configured ADC that does not exist, the MCP3008 only while a controller
    /// exists. Rebuilds <see cref="AnalogChannels"/> when an ADC configuration differed, also when this throws. Called
    /// under <see cref="_controllerLock"/>.
    /// </summary>
    /// <returns>The failure message of an ADC that could not be created, or null.</returns>
    private string? SynchronizeAdcs(Mcp3008Configuration? mcp3008, Ads1115Configuration? ads1115)
    {
        // Records have value equality, so an unchanged ADC configuration keeps its ADC.
        var isMcp3008Changed = mcp3008 != _appliedMcp3008Config;
        var isAds1115Changed = ads1115 != _appliedAds1115Config;

        // Disposed before the pins are discovered, so that an ADC releasing its pins cannot change a pin that is opened
        // as a GPIO pin again.
        if (isMcp3008Changed)
        {
            _mcp3008?.Dispose();
            _mcp3008 = null;
            _appliedMcp3008Config = null;
        }

        if (isAds1115Changed)
        {
            _ads1115?.Dispose();
            _ads1115 = null;
            _appliedAds1115Config = null;
        }

        if (!isMcp3008Changed && !isAds1115Changed)
        {
            DiscoverPins(mcp3008, ads1115);
            return null;
        }

        try
        {
            // Detaches and closes the pins a new ADC uses, so the ADC is created after this.
            DiscoverPins(mcp3008, ads1115);

            // An ADC that exists is unchanged and is kept. The MCP3008 waits for the controller, which reserves its pins.
            string? failureMessage = null;
            if (mcp3008 is not null && _mcp3008 is null && _controller is not null)
            {
                failureMessage = TryCreateMcp3008(mcp3008);
            }

            if (ads1115 is not null && _ads1115 is null)
            {
                failureMessage = CombineMessages(failureMessage, TryCreateAds1115(ads1115));
            }

            return failureMessage;
        }
        finally
        {
            AnalogChannels = CreateAnalogChannels();
        }
    }

    private static string? CombineMessages(string? first, string? second) =>
        first is null || second is null ? first ?? second : $"{first}; {second}";

    // Called under _controllerLock when the MCP3008 does not exist.
    private string? TryCreateMcp3008(Mcp3008Configuration configuration)
    {
        try
        {
            _mcp3008 = _createMcp3008(configuration);
            _appliedMcp3008Config = configuration with { };
            return null;
        }
        catch (Exception exception)
        {
            return $"MCP3008 init failed: {exception.Message}";
        }
    }

    // Called under _controllerLock when the ADS1115 does not exist.
    private string? TryCreateAds1115(Ads1115Configuration configuration)
    {
        try
        {
            _ads1115 = _createAds1115(configuration);
            _appliedAds1115Config = configuration with { };
            return null;
        }
        catch (Exception exception)
        {
            return $"ADS1115 init failed: {exception.Message}";
        }
    }

    private static Mcp3008 CreateMcp3008(Mcp3008Configuration configuration)
    {
        // Disposed when the constructor throws, which closes the pins it opened before, so that a retry can open them.
        var controller = new GpioController();
        SoftwareSpi spi;
        try
        {
            spi = new SoftwareSpi(
                configuration.ClockPin,
                configuration.MisoPin,
                configuration.MosiPin,
                configuration.ChipSelectPin,
                gpioController: controller,
                shouldDispose: true);
        }
        catch
        {
            controller.Dispose();
            throw;
        }

        try
        {
            return new Mcp3008(spi);
        }
        catch
        {
            spi.Dispose();
            throw;
        }
    }

    private static Ads1115 CreateAds1115(Ads1115Configuration configuration)
    {
        var i2cDevice = I2cDevice.Create(new I2cConnectionSettings(configuration.I2cBus, configuration.Address));
        try
        {
            return new Ads1115(i2cDevice);
        }
        catch
        {
            // The constructor writes to the device, which fails when no ADS1115 answers at the address.
            i2cDevice.Dispose();
            throw;
        }
    }

    // Called under _controllerLock. Creates the channels of the ADCs that exist.
    private Dictionary<int, AnalogChannel> CreateAnalogChannels()
    {
        var channels = new Dictionary<int, AnalogChannel>();

        if (_mcp3008 is not null)
        {
            for (var channelNumber = 0; channelNumber < 8; channelNumber++)
            {
                channels[channelNumber] = new AnalogChannel
                {
                    Source = AdcSource.Mcp3008,
                    ChannelNumber = channelNumber,
                    Status = ServiceStatus.Running
                };
            }
        }

        if (_ads1115 is not null)
        {
            for (var channelNumber = 0; channelNumber < 4; channelNumber++)
            {
                channels[8 + channelNumber] = new AnalogChannel
                {
                    Source = AdcSource.Ads1115,
                    ChannelNumber = channelNumber,
                    Status = ServiceStatus.Running
                };
            }
        }

        return channels;
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
        // Under the controller lock, so that an apply or Dispose cannot dispose an ADC during a read, which would set
        // its channel to Error for good or close the pins of a bit-banged read. Takes no pin lock.
        lock (_controllerLock)
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
            _appliedMcp3008Config = null;
            _appliedAds1115Config = null;
            _controller = null;
        }

        base.Dispose();
    }
}
