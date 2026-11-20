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
    private readonly bool? _isRaspberryPiBoard;
    private readonly Func<Mcp3008Configuration, Mcp3008> _createMcp3008;
    private readonly Func<Ads1115Configuration, Ads1115> _createAds1115;
    private readonly Action<int> _registerInterrupt;
    private readonly Action<int> _unregisterInterrupt;
    private readonly ConcurrentDictionary<int, PinChangeEventHandler> _interruptHandlers = new();
    private readonly SemaphoreSlim _connectedSignal = new(0, 1);

    // Guards the hardware fields below. Taken before a pin lock, never under one.
    private readonly Lock _controllerLock = new();

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

    private static readonly Lazy<bool> IsRaspberryPiModel = new(ReadIsRaspberryPiModel);

    // Guarded by _controllerLock. The hardware exists only between Connect and Stop. The attached pins are the
    // instances open on the controller, which a reload replaces in Pins.
    private GpioController? _controller;
    private bool _isControllerSimulated;
    private bool _isControllerRaspberryPi;
    private int _pinCount;
    private readonly Dictionary<int, GpioPin> _attachedPins = new();
    private Mcp3008? _mcp3008;
    private Ads1115? _ads1115;

    // Guarded by _controllerLock. The status message Connect published, which a successful poll restores after a
    // polling error.
    private string? _connectedStatusMessage;

    // Guarded by _controllerLock. The stopping token is canceled only after Dispose released the lock, so the service
    // loop and an applied configuration check this to create nothing after Dispose.
    private bool _isDisposed;

    /// <summary>
    /// Optional MCP3008 ADC configuration. Its pins are not opened as GPIO pins: they show
    /// <see cref="ServiceStatus.Unavailable"/> with the message "Used by the MCP3008".
    /// </summary>
    [Configuration]
    public partial Mcp3008Configuration? Mcp3008 { get; set; }

    /// <summary>
    /// Optional ADS1115 ADC configuration. On I2C bus 1 of a Raspberry Pi, any model, GPIO 2 (SDA) and 3 (SCL) are not
    /// opened as GPIO pins: they show <see cref="ServiceStatus.Unavailable"/> with the message "Used by I2C (ADS1115)".
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
    /// When true, uses a simulation driver instead of real hardware or the driver passed to the constructor.
    /// Enables GPIO functionality on non-Pi platforms for testing/development.
    /// </summary>
    [State(IsDiscrete = true, Position = 400)]
    [Configuration]
    public partial bool UseSimulation { get; set; }

    /// <summary>
    /// Auto-discovered GPIO pins indexed by pin number, which sets the <see cref="GpioPin.PinNumber"/> of each pin.
    /// A replaced dictionary takes effect when the configuration is applied. Null is stored as an empty dictionary, so
    /// every pin then starts like a new pin.
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
    /// <param name="driver">Optional GPIO driver for the hardware, used unless <see cref="UseSimulation"/> is set, which the subject disposes when it is disposed. If null, uses the system default.</param>
    public GpioSubject(GpioDriver? driver = null)
        : this(driver, driver is RaspberryPi3Driver)
    {
    }

    /// <summary>
    /// Creates a GpioSubject with an optional GPIO driver that drives the pins of a board that uses the Raspberry Pi
    /// register driver when <paramref name="isRaspberryPi"/> is true, so that new pins start with its power-on pull.
    /// The ADCs are created with <paramref name="createMcp3008"/> and <paramref name="createAds1115"/>, by default on
    /// the hardware. <paramref name="isRaspberryPiBoard"/> tells whether the board is a Raspberry Pi of any model; when
    /// null, it is read from the device tree.
    /// </summary>
    internal GpioSubject(
        GpioDriver? driver,
        bool isRaspberryPi,
        Func<Mcp3008Configuration, Mcp3008>? createMcp3008 = null,
        Func<Ads1115Configuration, Ads1115>? createAds1115 = null,
        bool? isRaspberryPiBoard = null)
    {
        _driver = driver;
        _isDriverRaspberryPi = isRaspberryPi;
        _isRaspberryPiBoard = isRaspberryPiBoard;
        _createMcp3008 = createMcp3008 ?? CreateMcp3008;
        _createAds1115 = createAds1115 ?? CreateAds1115;
        _registerInterrupt = RegisterInterrupt;
        _unregisterInterrupt = UnregisterInterrupt;

        PollingInterval = TimeSpan.FromSeconds(5);
        RetryInterval = TimeSpan.FromSeconds(30);

        Pins = new Dictionary<int, GpioPin>();
        AnalogChannels = new Dictionary<int, AnalogChannel>();

        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }

    /// <summary>
    /// Applies the defaults of a board that uses the Raspberry Pi register driver to the unconfigured pins,
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
    /// Gets the mode with the pull a board that uses the Raspberry Pi register driver applies at power-on: up for GPIO
    /// 0 to 8, down for GPIO 9 to 27, and plain <see cref="GpioPinMode.Input"/> above.
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
    /// and 3 (SCL) when <paramref name="isI2cBus1Reserved"/>.
    /// </summary>
    private static string? GetReservedPinMessage(int pinNumber, Mcp3008Configuration? mcp3008, bool isI2cBus1Reserved)
    {
        if (mcp3008 is not null && IsMcp3008Pin(mcp3008, pinNumber))
        {
            return "Used by the MCP3008";
        }

        if (isI2cBus1Reserved && pinNumber is 2 or 3)
        {
            return "Used by I2C (ADS1115)";
        }

        return null;
    }

    private static bool ReadIsRaspberryPiModel()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return File.ReadAllText("/proc/device-tree/model").StartsWith("Raspberry Pi", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A container can hide the device tree.
            return false;
        }
    }

    partial void OnPinsChanging(ref Dictionary<int, GpioPin> newValue, ref bool cancel)
    {
        newValue ??= new Dictionary<int, GpioPin>();
    }

    partial void OnPinsChanged(Dictionary<int, GpioPin> newValue)
    {
        foreach (var (pinNumber, pin) in newValue)
        {
            pin.PinNumber = pinNumber;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var result = EnsureConnected();
                if (result == InitializationResult.Success)
                {
                    try
                    {
                        VerifyPins();
                        PollAdcChannels();
                        ClearPollingError();
                        await Task.Delay(PollingInterval, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        SetPollingError(exception);
                        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    }
                }
                else
                {
                    // An applied configuration that connects, such as one that enables the simulation, ends the wait.
                    var timeout = result == InitializationResult.PermanentFailure ? Timeout.InfiniteTimeSpan : RetryInterval;
                    await _connectedSignal.WaitAsync(timeout, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The service stops.
        }

        lock (_controllerLock)
        {
            if (!_isDisposed)
            {
                Status = ServiceStatus.Stopping;
                StatusMessage = "Shutting down...";
            }
        }
    }

    private void SetPollingError(Exception exception)
    {
        lock (_controllerLock)
        {
            if (_isDisposed)
            {
                return;
            }

            StatusMessage = $"Polling error: {exception.Message}";
            Status = ServiceStatus.Error;
        }
    }

    private void ClearPollingError()
    {
        lock (_controllerLock)
        {
            // While connected, only a polling error sets Error. Dispose releases the controller.
            if (Status != ServiceStatus.Error || _controller is null)
            {
                return;
            }

            StatusMessage = _connectedStatusMessage;
            Status = ServiceStatus.Running;
        }
    }

    private enum InitializationResult
    {
        Success,
        TransientFailure,
        PermanentFailure
    }

    private InitializationResult EnsureConnected()
    {
        lock (_controllerLock)
        {
            if (_isDisposed)
            {
                return InitializationResult.PermanentFailure;
            }

            // An applied configuration connects by itself.
            if (_controller is not null)
            {
                return InitializationResult.Success;
            }

            StatusMessage = "Initializing GPIO controller...";
            Status = ServiceStatus.Starting;
            return Connect(failureMessages: []);
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

            var failureMessages = new List<string>();
            Stop(keepController: _isControllerSimulated == UseSimulation, failureMessages);
            if (Connect(failureMessages) == InitializationResult.Success && _connectedSignal.CurrentCount == 0)
            {
                _connectedSignal.Release();
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates the controller unless Stop kept one, opens the pins of <see cref="Pins"/> in their mode except those
    /// reserved for a configured ADC, and then creates the configured ADCs. A pin that Stop left open as an output on
    /// the kept controller keeps its line when it stays an output, and is closed first otherwise. Sets
    /// <see cref="Status"/> and reports <paramref name="failureMessages"/> and those of the pins and ADCs in
    /// <see cref="StatusMessage"/>. Called under <see cref="_controllerLock"/> with no pin attached and no ADC.
    /// </summary>
    private InitializationResult Connect(List<string> failureMessages)
    {
        try
        {
            if (_controller is null)
            {
                (_controller, _isControllerSimulated, _isControllerRaspberryPi) = CreateController();
                _pinCount = _controller.PinCount;
            }
        }
        catch (PlatformNotSupportedException)
        {
            ReleaseController(failureMessages);
            AnalogChannels = new Dictionary<int, AnalogChannel>();
            StatusMessage = "GPIO not supported on this platform";
            Status = ServiceStatus.Unavailable;
            return InitializationResult.PermanentFailure;
        }
        catch (Exception exception)
        {
            ReleaseController(failureMessages);
            AnalogChannels = new Dictionary<int, AnalogChannel>();
            StatusMessage = $"Failed to initialize GPIO: {exception.Message}";
            Status = ServiceStatus.Error;
            return InitializationResult.TransientFailure;
        }

        var controller = _controller;
        var pinCount = _pinCount;
        var isRaspberryPi = _isControllerRaspberryPi;
        var existingPins = Pins;
        var pins = new Dictionary<int, GpioPin>(pinCount);
        var isEveryPinConfigured = existingPins.Count == pinCount;
        for (var pinNumber = 0; pinNumber < pinCount; pinNumber++)
        {
            if (!existingPins.TryGetValue(pinNumber, out var pin))
            {
                isEveryPinConfigured = false;
                pin = new GpioPin
                {
                    PinNumber = pinNumber,
                    Mode = isRaspberryPi ? GetRaspberryPiPowerOnMode(pinNumber) : GpioPinMode.Input
                };
            }

            pins[pinNumber] = pin;
        }

        // A reload replaces Pins without this lock and applies afterwards, which connects again with its pins.
        if (!isEveryPinConfigured && ReferenceEquals(Pins, existingPins))
        {
            Pins = pins;
        }

        var mcp3008 = Mcp3008;
        var ads1115 = Ads1115;
        var isI2cBus1Reserved = ads1115 is { I2cBus: 1 } && !_isControllerSimulated &&
            (isRaspberryPi || (_isRaspberryPiBoard ?? IsRaspberryPiModel.Value));
        foreach (var (pinNumber, pin) in pins)
        {
            // A throwing subscriber of a pin must not keep the other pins from starting.
            try
            {
                var reservedMessage = GetReservedPinMessage(pinNumber, mcp3008, isI2cBus1Reserved);
                if (reservedMessage is null)
                {
                    _attachedPins[pinNumber] = pin;
                    pin.Attach(controller, _registerInterrupt, _unregisterInterrupt);
                    continue;
                }

                // Closed before the ADCs are created, which open it.
                ClosePin(pinNumber, failureMessages);

                pin.StatusMessage = reservedMessage;
                pin.Status = ServiceStatus.Unavailable;
            }
            catch (Exception exception)
            {
                var message = $"GPIO pin {pinNumber} could not be started: {exception.Message}";
                failureMessages.Add(message);
                TrySetError(pin, message);
            }
        }

        if (mcp3008 is not null)
        {
            try
            {
                _mcp3008 = _createMcp3008(mcp3008);
            }
            catch (Exception exception)
            {
                failureMessages.Add($"MCP3008 init failed: {exception.Message}");
            }
        }

        if (ads1115 is not null)
        {
            try
            {
                _ads1115 = _createAds1115(ads1115);
            }
            catch (Exception exception)
            {
                failureMessages.Add($"ADS1115 init failed: {exception.Message}");
            }
        }

        // Read before they are published, so that a restart never publishes a reading of 0.
        var analogChannels = CreateAnalogChannels();
        ReadAnalogChannels(analogChannels.Values);
        AnalogChannels = analogChannels;

        // Set before the status, so that Running is published with its message.
        _connectedStatusMessage = failureMessages.Count > 0 ? string.Join("; ", failureMessages) : null;
        StatusMessage = _connectedStatusMessage;
        Status = ServiceStatus.Running;
        return InitializationResult.Success;
    }

    /// <summary>
    /// Detaches the attached pins, unregisters their interrupts and closes them, disposes the ADCs and releases the
    /// controller. With <paramref name="keepController"/>, the controller is kept and a pin open as an output stays
    /// open, so that its line keeps its level. Never throws: a failure is added to <paramref name="failureMessages"/>
    /// and the rest is still released. Called under <see cref="_controllerLock"/>.
    /// </summary>
    private void Stop(bool keepController, List<string> failureMessages)
    {
        if (_controller is null)
        {
            return;
        }

        foreach (var (pinNumber, pin) in _attachedPins)
        {
            // Detached before its interrupt is unregistered, so that a mode change cannot register a handler afterwards.
            try
            {
                pin.Detach();
            }
            catch (Exception exception)
            {
                failureMessages.Add($"GPIO pin {pinNumber} could not be stopped: {exception.Message}");
            }

            try
            {
                UnregisterInterrupt(pinNumber);
            }
            catch (Exception exception)
            {
                failureMessages.Add($"GPIO pin {pinNumber} could not be released: {exception.Message}");
            }

            try
            {
                if (_controller.IsPinOpen(pinNumber) && !(keepController && _controller.GetPinMode(pinNumber) == PinMode.Output))
                {
                    _controller.ClosePin(pinNumber);
                }
            }
            catch (Exception exception)
            {
                failureMessages.Add($"GPIO pin {pinNumber} could not be closed: {exception.Message}");
            }
        }

        _attachedPins.Clear();

        try
        {
            _mcp3008?.Dispose();
        }
        catch (Exception exception)
        {
            failureMessages.Add($"MCP3008 could not be stopped: {exception.Message}");
        }

        try
        {
            _ads1115?.Dispose();
        }
        catch (Exception exception)
        {
            failureMessages.Add($"ADS1115 could not be stopped: {exception.Message}");
        }

        _mcp3008 = null;
        _ads1115 = null;
        if (!keepController)
        {
            ReleaseController(failureMessages);
        }
    }

    // Called under _controllerLock with the controller.
    private void ClosePin(int pinNumber, List<string> failureMessages)
    {
        try
        {
            if (_controller!.IsPinOpen(pinNumber))
            {
                _controller.ClosePin(pinNumber);
            }
        }
        catch (Exception exception)
        {
            failureMessages.Add($"GPIO pin {pinNumber} could not be closed: {exception.Message}");
        }
    }

    // Called under _controllerLock. A controller disposes its driver, so the one over an injected driver is dropped
    // instead, and the next controller uses that driver again.
    private void ReleaseController(List<string> failureMessages)
    {
        if (_controller is null)
        {
            return;
        }

        if (_driver is not null && !_isControllerSimulated)
        {
            _controller = null;
            return;
        }

        var controller = _controller;
        _controller = null;
        try
        {
            controller.Dispose();
        }
        catch (Exception exception)
        {
            failureMessages.Add($"GPIO controller could not be disposed: {exception.Message}");
        }
    }

    // Never throws: a throwing subscriber of the status must not abort the caller, which reported the failure itself.
    private static void TrySetError(GpioPin pin, string message)
    {
        try
        {
            pin.StatusMessage = message;
            pin.Status = ServiceStatus.Error;
        }
        catch (Exception)
        {
            // Nothing is left to report it to.
        }
    }

    private (GpioController Controller, bool IsSimulated, bool IsRaspberryPi) CreateController()
    {
        if (UseSimulation)
        {
            return (new GpioController(PinNumberingScheme.Logical, new SimulationGpioDriver()), true, false);
        }

        if (_driver is not null)
        {
            return (new GpioController(PinNumberingScheme.Logical, _driver), false, _isDriverRaspberryPi);
        }

        var raspberryPiDriver = TryCreateRaspberryPiDriver();
        return raspberryPiDriver is not null
            ? (new GpioController(PinNumberingScheme.Logical, raspberryPiDriver), false, true)
            : (new GpioController(), false, false);
    }

    /// <summary>
    /// Creates the Raspberry Pi register driver on a board that uses it: a Raspberry Pi 3, 4, 400, Zero 2 W or Compute
    /// Module 3 or 4. Returns null on other boards.
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

    private void VerifyPins()
    {
        foreach (var pin in Pins.Values)
        {
            pin.PollHardwareLevel();
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
        PinChangeEventHandler handler = (_, arguments) =>
        {
            if (!Pins.TryGetValue(arguments.PinNumber, out var pin))
            {
                return;
            }

            // Runs on the driver's thread, where a thrown exception would end the process.
            try
            {
                pin.ApplyHardwareLevel(arguments.ChangeType == PinEventTypes.Rising);
            }
            catch (Exception exception)
            {
                TrySetError(pin, $"Failed to apply an edge of GPIO pin {arguments.PinNumber}: {exception.Message}");
            }
        };

        // Called by an attached pin, so the controller exists: Stop releases it only after detaching every pin. Stored
        // only once registered, so that Stop never removes a handler that the driver refused.
        _controller!.RegisterCallbackForPinValueChangedEvent(
            pinNumber,
            PinEventTypes.Rising | PinEventTypes.Falling,
            handler);
        _interruptHandlers[pinNumber] = handler;
    }

    private void UnregisterInterrupt(int pinNumber)
    {
        // Removed also when the driver throws: a handler left registered applies an edge of the pin of its number,
        // which the handler registered by the next Connect applies too, so it changes nothing.
        if (_interruptHandlers.TryRemove(pinNumber, out var handler))
        {
            _controller!.UnregisterCallbackForPinValueChangedEvent(pinNumber, handler);
        }
    }

    private void PollAdcChannels()
    {
        // Under the controller lock, so that an apply or Dispose never disposes an ADC during a read.
        lock (_controllerLock)
        {
            ReadAnalogChannels(AnalogChannels.Values);
        }
    }

    // Called under _controllerLock.
    private void ReadAnalogChannels(IEnumerable<AnalogChannel> channels)
    {
        foreach (var channel in channels)
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
                    var raw = _ads1115.ReadRaw(GetSingleEndedInput(channel.ChannelNumber));
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

    // The enum values 0 to 3 are differential pairs, such as AIN0_AIN1; the single-ended inputs AIN0 to AIN3 follow.
    private static InputMultiplexer GetSingleEndedInput(int channelNumber) => InputMultiplexer.AIN0 + channelNumber;

    public override void Dispose()
    {
        try
        {
            lock (_controllerLock)
            {
                _isDisposed = true;
                try
                {
                    Stop(keepController: false, []);
                    StatusMessage = null;
                    Status = ServiceStatus.Stopped;
                }
                finally
                {
                    _driver?.Dispose();
                }
            }
        }
        finally
        {
            // Cancels the stopping token, which ends the service loop.
            base.Dispose();
        }
    }
}
