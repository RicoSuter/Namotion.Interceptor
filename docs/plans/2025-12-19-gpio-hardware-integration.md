# GPIO Hardware Integration Plan

Goal: Implement actual hardware integration for Namotion.Devices.Gpio with full bidirectional sync.

## Decisions from Brainstorming

| Question | Decision |
|----------|----------|
| Q1 Platform behavior | Silent empty state + `ServiceStatus.Unavailable` with status message |
| Q2 Input reading | Interrupts as primary + verification polling every `PollingInterval` |
| Q3 Output writing | Direct write in interceptor + read-back verification |
| Q4 Pin discovery | Add all BCM 0-27 pins, each with individual `ServiceStatus` |
| Q5 Polling interval | Single `PollingInterval` property (default 5s) shared for GPIO verification + ADC |
| Q6 Initial state | Read all pin values on startup |
| Q7 Mode changes | Register/unregister interrupts, sync values on mode change |

## Data Flow Summary

| Scenario | Direction | Mechanism |
|----------|-----------|-----------|
| Digital Input | HW → SW | Interrupts + polling verification |
| Digital Output | SW → HW | Write interceptor + read-back verify |
| Digital Output | HW → SW | Polling detects external changes |
| Analog Input | HW → SW | Polling reads ADC values |
| Mode: Input→Output | SW → HW | Unregister interrupt, set output value |
| Mode: Output→Input | SW → HW | Register interrupt, read current value |
| Startup | HW → SW | Read all pin values |

## Current State

**Exists:**
- GpioSubject, GpioPin, AnalogChannel data models
- Mcp3008Configuration, Ads1115Configuration
- Blazor UI components
- Basic unit tests

**Missing:**
- ServiceStatus enum and IHostedSubject interface (new abstraction)
- GpioController initialization
- Pin discovery with availability status
- Hardware read/write with interceptors
- ADC support
- Mode change handling

---

## Implementation Tasks

### Task 0: Add ServiceStatus and IHostedSubject abstraction

**Files:**
- `src/HomeBlaze/HomeBlaze.Abstractions/ServiceStatus.cs` (new, replaces ServerStatus.cs)
- `src/HomeBlaze/HomeBlaze.Abstractions/IHostedSubject.cs` (new)
- `src/HomeBlaze/HomeBlaze.Abstractions/IServerSubject.cs` (update)

**ServiceStatus.cs:**
```csharp
namespace HomeBlaze.Abstractions;

/// <summary>
/// Status of a hosted service, device, or server.
/// </summary>
public enum ServiceStatus
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Unavailable,
    Error
}
```

**IHostedSubject.cs:**
```csharp
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions;

/// <summary>
/// Base interface for subjects with lifecycle status (services, devices, servers).
/// </summary>
public interface IHostedSubject
{
    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    [State]
    ServiceStatus Status { get; }

    /// <summary>
    /// Human-readable status message (error details, progress info, etc.).
    /// </summary>
    [State]
    string? StatusMessage { get; }
}
```

**IServerSubject.cs (update):**
```csharp
namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that act as servers (OPC UA, MQTT, etc.).
/// </summary>
public interface IServerSubject : IHostedSubject
{
}
```

**Migration:**
- Update OpcUaServer to use `ServiceStatus` instead of `ServerStatus`
- Delete `ServerStatus.cs` after migration

---

### Task 1: Update GpioPin with ServiceStatus

**File:** `src/Namotion.Devices.Gpio/GpioPin.cs`

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents a single GPIO pin with mode, value, and availability status.
/// </summary>
[InterceptorSubject]
public partial class GpioPin : IHostedSubject
{
    /// <summary>
    /// The GPIO pin number (BCM numbering).
    /// </summary>
    public partial int PinNumber { get; set; }

    /// <summary>
    /// The pin operating mode.
    /// </summary>
    [Configuration]
    public partial GpioPinMode Mode { get; set; }

    /// <summary>
    /// The current pin value (true = high, false = low).
    /// </summary>
    [State]
    public partial bool Value { get; set; }

    /// <summary>
    /// Pin availability status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Status message (e.g., "Reserved for I2C", "Write verification failed").
    /// </summary>
    [State]
    public partial string? StatusMessage { get; set; }

    public GpioPin()
    {
        PinNumber = 0;
        Mode = GpioPinMode.Input;
        Value = false;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }
}
```

---

### Task 2: Update GpioSubject with ServiceStatus and PollingInterval

**File:** `src/Namotion.Devices.Gpio/GpioSubject.cs`

Add/update properties:

```csharp
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
```

Update constructor:
```csharp
public GpioSubject()
{
    Pins = new Dictionary<int, GpioPin>();
    AnalogChannels = new Dictionary<int, AnalogChannel>();
    PollingInterval = TimeSpan.FromSeconds(5);
    Status = ServiceStatus.Stopped;
}
```

Implement `IHostedSubject` on the class.

---

### Task 3: Create GpioWriteInterceptor

**File:** `src/Namotion.Devices.Gpio/Interceptors/GpioWriteInterceptor.cs`

```csharp
using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Devices.Gpio.Interceptors;

/// <summary>
/// Writes pin value to hardware when GpioPin.Value changes (output mode only).
/// Includes read-back verification.
/// </summary>
public class GpioWriteInterceptor : IWriteInterceptor
{
    private readonly GpioController _controller;

    public GpioWriteInterceptor(GpioController controller)
    {
        _controller = controller;
    }

    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WritePropertyDelegate<T> next)
    {
        next(ref context);

        if (context.Subject is not GpioPin pin)
            return;

        if (context.Property.Name != nameof(GpioPin.Value))
            return;

        if (pin.Mode != GpioPinMode.Output)
            return;

        if (pin.Status != ServiceStatus.Running)
            return;

        // Write to hardware
        var pinValue = pin.Value ? PinValue.High : PinValue.Low;
        _controller.Write(pin.PinNumber, pinValue);

        // Read-back verification
        var actualValue = _controller.Read(pin.PinNumber) == PinValue.High;
        if (actualValue != pin.Value)
        {
            pin.Value = actualValue;
            pin.Status = ServiceStatus.Error;
            pin.StatusMessage = "Write verification failed - possible short circuit or external driver";
        }
    }
}
```

---

### Task 4: Create GpioModeChangeInterceptor

**File:** `src/Namotion.Devices.Gpio/Interceptors/GpioModeChangeInterceptor.cs`

```csharp
using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Interceptor;

namespace Namotion.Devices.Gpio.Interceptors;

/// <summary>
/// Handles pin mode changes: reconfigures hardware, manages interrupts.
/// </summary>
public class GpioModeChangeInterceptor : IWriteInterceptor
{
    private readonly GpioController _controller;
    private readonly Action<int> _registerInterrupt;
    private readonly Action<int> _unregisterInterrupt;

    public GpioModeChangeInterceptor(
        GpioController controller,
        Action<int> registerInterrupt,
        Action<int> unregisterInterrupt)
    {
        _controller = controller;
        _registerInterrupt = registerInterrupt;
        _unregisterInterrupt = unregisterInterrupt;
    }

    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WritePropertyDelegate<T> next)
    {
        var oldMode = context.Subject is GpioPin pin ? pin.Mode : default;

        next(ref context);

        if (context.Subject is not GpioPin changedPin)
            return;

        if (context.Property.Name != nameof(GpioPin.Mode))
            return;

        if (changedPin.Status != ServiceStatus.Running)
            return;

        var newMode = changedPin.Mode;
        if (oldMode == newMode)
            return;

        // Handle mode transition
        if (oldMode == GpioPinMode.Input && newMode == GpioPinMode.Output)
        {
            // Input → Output: unregister interrupt, set pin mode
            _unregisterInterrupt(changedPin.PinNumber);
            _controller.SetPinMode(changedPin.PinNumber, PinMode.Output);
            // Write current Value to hardware
            _controller.Write(changedPin.PinNumber, changedPin.Value ? PinValue.High : PinValue.Low);
        }
        else if (oldMode == GpioPinMode.Output && newMode == GpioPinMode.Input)
        {
            // Output → Input: set pin mode, read value, register interrupt
            _controller.SetPinMode(changedPin.PinNumber, PinMode.Input);
            changedPin.Value = _controller.Read(changedPin.PinNumber) == PinValue.High;
            _registerInterrupt(changedPin.PinNumber);
        }
        else
        {
            // Handle other modes (InputPullUp, InputPullDown)
            var hardwareMode = newMode switch
            {
                GpioPinMode.Input => PinMode.Input,
                GpioPinMode.InputPullUp => PinMode.InputPullUp,
                GpioPinMode.InputPullDown => PinMode.InputPullDown,
                GpioPinMode.Output => PinMode.Output,
                _ => PinMode.Input
            };
            _controller.SetPinMode(changedPin.PinNumber, hardwareMode);

            if (newMode != GpioPinMode.Output)
            {
                changedPin.Value = _controller.Read(changedPin.PinNumber) == PinValue.High;
                _registerInterrupt(changedPin.PinNumber);
            }
        }
    }
}
```

---

### Task 5: Implement ExecuteAsync in GpioSubject

**File:** `src/Namotion.Devices.Gpio/GpioSubject.cs`

```csharp
private GpioController? _controller;
private readonly ConcurrentDictionary<int, PinChangeEventHandler> _interruptHandlers = new();

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
        return;
    }
    catch (Exception ex)
    {
        Status = ServiceStatus.Error;
        StatusMessage = $"Failed to initialize GPIO: {ex.Message}";
        return;
    }

    // Register interceptors on this subject's context
    Context.AddService<IWriteInterceptor>(new GpioWriteInterceptor(_controller));
    Context.AddService<IWriteInterceptor>(new GpioModeChangeInterceptor(
        _controller,
        RegisterInterrupt,
        UnregisterInterrupt));

    // Discover all BCM pins 0-27
    for (int pinNumber = 0; pinNumber <= 27; pinNumber++)
    {
        var pin = new GpioPin(Context)
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
        catch (Exception ex)
        {
            pin.Status = ServiceStatus.Unavailable;
            pin.StatusMessage = ex.Message; // e.g., "Pin reserved for I2C"
        }

        Pins[pinNumber] = pin;
    }

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
            await PollAdcChannelsAsync();

            await Task.Delay(PollingInterval, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            Status = ServiceStatus.Error;
            StatusMessage = $"Polling error: {ex.Message}";
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    Status = ServiceStatus.Stopping;
    StatusMessage = "Shutting down...";
}

private void RegisterInterrupt(int pinNumber)
{
    if (_controller == null) return;

    PinChangeEventHandler handler = (sender, args) =>
    {
        if (Pins.TryGetValue(args.PinNumber, out var pin) && pin.Status == ServiceStatus.Running)
        {
            pin.Value = args.ChangeType == PinEventTypes.Rising;
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
```

---

### Task 6: Implement ApplyConfigurationAsync for ADC

**File:** `src/Namotion.Devices.Gpio/GpioSubject.cs`

```csharp
private Mcp3008? _mcp3008;
private Ads1115? _ads1115;

public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
{
    // Dispose existing ADC
    _mcp3008?.Dispose();
    _ads1115?.Dispose();
    _mcp3008 = null;
    _ads1115 = null;
    AnalogChannels.Clear();

    // Initialize MCP3008 if configured
    if (Mcp3008 != null)
    {
        try
        {
            _mcp3008 = new Mcp3008(
                SoftwareSpi.Create(
                    Mcp3008.ClockPin,
                    Mcp3008.MisoPin,
                    Mcp3008.MosiPin,
                    Mcp3008.ChipSelectPin));

            for (int i = 0; i < 8; i++)
            {
                AnalogChannels[i] = new AnalogChannel(Context)
                {
                    ChannelNumber = i,
                    Status = ServiceStatus.Running
                };
            }
        }
        catch (Exception ex)
        {
            // Log error, channels remain empty
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
                AnalogChannels[i] = new AnalogChannel(Context)
                {
                    ChannelNumber = i,
                    Status = ServiceStatus.Running
                };
            }
        }
        catch (Exception ex)
        {
            // Log error, channels remain empty
        }
    }

    return Task.CompletedTask;
}

private Task PollAdcChannelsAsync()
{
    if (_mcp3008 != null)
    {
        foreach (var channel in AnalogChannels.Values)
        {
            var raw = _mcp3008.Read(channel.ChannelNumber);
            channel.RawValue = raw;
            channel.Value = raw / 1023.0;
        }
    }

    if (_ads1115 != null)
    {
        foreach (var channel in AnalogChannels.Values)
        {
            var raw = _ads1115.ReadRaw((InputMultiplexer)channel.ChannelNumber);
            channel.RawValue = raw;
            channel.Value = raw / 32767.0;
        }
    }

    return Task.CompletedTask;
}
```

---

### Task 7: Update AnalogChannel with ServiceStatus

**File:** `src/Namotion.Devices.Gpio/AnalogChannel.cs`

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

[InterceptorSubject]
public partial class AnalogChannel : IHostedSubject
{
    public partial int ChannelNumber { get; set; }

    [State]
    public partial double Value { get; set; }

    [State]
    public partial int RawValue { get; set; }

    [State]
    public partial ServiceStatus Status { get; set; }

    [State]
    public partial string? StatusMessage { get; set; }

    public AnalogChannel()
    {
        ChannelNumber = 0;
        Value = 0.0;
        RawValue = 0;
        Status = ServiceStatus.Stopped;
        StatusMessage = null;
    }
}
```

---

### Task 8: Implement IDisposable

**File:** `src/Namotion.Devices.Gpio/GpioSubject.cs`

```csharp
public override void Dispose()
{
    Status = ServiceStatus.Stopped;
    StatusMessage = null;

    // Unregister all interrupts
    foreach (var pinNumber in _interruptHandlers.Keys.ToList())
    {
        UnregisterInterrupt(pinNumber);
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
```

---

### Task 9: Update GpioPinMode enum

**File:** `src/Namotion.Devices.Gpio/GpioPinMode.cs`

```csharp
namespace Namotion.Devices.Gpio;

public enum GpioPinMode
{
    Input,
    InputPullUp,
    InputPullDown,
    Output
}
```

---

### Task 10: Add required usings and dependencies

**File:** `src/Namotion.Devices.Gpio/GpioSubject.cs`

Add usings:
```csharp
using System.Collections.Concurrent;
using System.Device.Gpio;
using System.Device.I2c;
using System.Device.Spi;
using Iot.Device.Adc;
using HomeBlaze.Abstractions;
```

---

### Task 11: Update OpcUaServer to use ServiceStatus

**File:** `src/HomeBlaze/HomeBlaze.Servers.OpcUa/OpcUaServer.cs`

- Change `ServerStatus` → `ServiceStatus`
- Remove `IServerSubject` implementation (now inherited via `IHostedSubject`)

---

### Task 12: Delete ServerStatus.cs

**File:** `src/HomeBlaze/HomeBlaze.Abstractions/ServerStatus.cs`

Delete after OpcUaServer migration is complete.

---

### Task 13: Add/update tests

**Files:**
- `src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs`
- `src/Namotion.Devices.Gpio.Tests/GpioPinTests.cs`

Test scenarios:
- On Windows, Status = Unavailable, Pins all have Status = Unavailable
- Pin mode changes trigger appropriate callbacks
- Write interceptor verifies values

---

## Dependencies

Already referenced in csproj:
- `System.Device.Gpio` (3.*)
- `Iot.Device.Bindings` (3.*)

---

## Testing Strategy

1. **Unit tests** - Run on Windows with Unavailable status (graceful degradation)
2. **Integration tests** - Manual testing on Raspberry Pi hardware
3. **Mock tests** - Future: mock GpioController for automated integration tests
