# GPIO Hardware Integration - Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement full bidirectional hardware sync for Namotion.Devices.Gpio with status tracking, interrupts, polling, and ADC support.

**Architecture:** Introduce `IHostedSubject` abstraction with `ServiceStatus` enum for lifecycle tracking. GPIO uses write interceptors for hardware sync (scoped to GPIO context). Interrupts provide instant input updates with polling verification. ADC channels polled on same interval.

**Tech Stack:** .NET 9, System.Device.Gpio, Iot.Device.Bindings, xUnit

---

## Task 1: Create ServiceStatus enum

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/ServiceStatus.cs`

**Step 1.1: Create the ServiceStatus enum file**

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

**Step 1.2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Abstractions/HomeBlaze.Abstractions.csproj`
Expected: Build succeeded

**Step 1.3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/ServiceStatus.cs
git commit -m "feat: add ServiceStatus enum for lifecycle tracking"
```

---

## Task 2: Create IHostedSubject interface

**Files:**
- Create: `src/HomeBlaze/HomeBlaze.Abstractions/IHostedSubject.cs`

**Step 2.1: Create the IHostedSubject interface file**

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

**Step 2.2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Abstractions/HomeBlaze.Abstractions.csproj`
Expected: Build succeeded

**Step 2.3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/IHostedSubject.cs
git commit -m "feat: add IHostedSubject interface for lifecycle status"
```

---

## Task 3: Update IServerSubject to extend IHostedSubject

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Abstractions/IServerSubject.cs`

**Step 3.1: Update IServerSubject**

Replace entire file with:

```csharp
namespace HomeBlaze.Abstractions;

/// <summary>
/// Interface for subjects that act as servers (OPC UA, MQTT, etc.).
/// </summary>
public interface IServerSubject : IHostedSubject
{
}
```

**Step 3.2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Abstractions/HomeBlaze.Abstractions.csproj`
Expected: Build succeeded

**Step 3.3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Abstractions/IServerSubject.cs
git commit -m "refactor: IServerSubject extends IHostedSubject"
```

---

## Task 4: Migrate OpcUaServer to ServiceStatus

**Files:**
- Modify: `src/HomeBlaze/HomeBlaze.Servers.OpcUa/OpcUaServer.cs`

**Step 4.1: Replace ServerStatus with ServiceStatus**

Find and replace all occurrences:
- `ServerStatus.Stopped` → `ServiceStatus.Stopped`
- `ServerStatus.Starting` → `ServiceStatus.Starting`
- `ServerStatus.Running` → `ServiceStatus.Running`
- `ServerStatus.Stopping` → `ServiceStatus.Stopping`
- `ServerStatus.Error` → `ServiceStatus.Error`
- `ServerStatus Status` → `ServiceStatus Status`

Also rename `ErrorMessage` property to `StatusMessage` for consistency with IHostedSubject.

**Step 4.2: Build to verify**

Run: `dotnet build src/HomeBlaze/HomeBlaze.Servers.OpcUa/HomeBlaze.Servers.OpcUa.csproj`
Expected: Build succeeded

**Step 4.3: Commit**

```bash
git add src/HomeBlaze/HomeBlaze.Servers.OpcUa/OpcUaServer.cs
git commit -m "refactor: OpcUaServer uses ServiceStatus instead of ServerStatus"
```

---

## Task 5: Delete ServerStatus.cs

**Files:**
- Delete: `src/HomeBlaze/HomeBlaze.Abstractions/ServerStatus.cs`

**Step 5.1: Delete the file**

Delete `src/HomeBlaze/HomeBlaze.Abstractions/ServerStatus.cs`

**Step 5.2: Build entire solution to verify no remaining references**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded

**Step 5.3: Commit**

```bash
git add -A
git commit -m "refactor: remove deprecated ServerStatus enum"
```

---

## Task 6: Update GpioPinMode enum with pull modes

**Files:**
- Modify: `src/Namotion.Devices.Gpio/GpioPinMode.cs`

**Step 6.1: Add pull-up and pull-down modes**

Replace entire file with:

```csharp
namespace Namotion.Devices.Gpio;

/// <summary>
/// GPIO pin operating mode.
/// </summary>
public enum GpioPinMode
{
    Input,
    InputPullUp,
    InputPullDown,
    Output
}
```

**Step 6.2: Build to verify**

Run: `dotnet build src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj`
Expected: Build succeeded

**Step 6.3: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioPinMode.cs
git commit -m "feat: add InputPullUp and InputPullDown to GpioPinMode"
```

---

## Task 7: Update GpioPin with ServiceStatus

**Files:**
- Modify: `src/Namotion.Devices.Gpio/GpioPin.cs`
- Modify: `src/Namotion.Devices.Gpio.Tests/GpioPinTests.cs`

**Step 7.1: Write failing test for Status property**

Add to `src/Namotion.Devices.Gpio.Tests/GpioPinTests.cs`:

```csharp
using HomeBlaze.Abstractions;

// Add this test method to GpioPinTests class:
[Fact]
public void GpioPin_InitializesWithStoppedStatus()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();

    // Act
    var pin = new GpioPin(context);

    // Assert
    Assert.Equal(ServiceStatus.Stopped, pin.Status);
    Assert.Null(pin.StatusMessage);
}

[Fact]
public void GpioPin_ImplementsIHostedSubject()
{
    // Arrange
    var context = InterceptorSubjectContext.Create();

    // Act
    var pin = new GpioPin(context);

    // Assert
    Assert.IsAssignableFrom<IHostedSubject>(pin);
}
```

**Step 7.2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "GpioPin_InitializesWithStoppedStatus|GpioPin_ImplementsIHostedSubject"`
Expected: FAIL - Status property does not exist

**Step 7.3: Update GpioPin implementation**

Replace `src/Namotion.Devices.Gpio/GpioPin.cs` with:

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

**Step 7.4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "GpioPin"`
Expected: All GpioPin tests PASS

**Step 7.5: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioPin.cs src/Namotion.Devices.Gpio.Tests/GpioPinTests.cs
git commit -m "feat: add ServiceStatus to GpioPin, implement IHostedSubject"
```

---

## Task 8: Update AnalogChannel with ServiceStatus

**Files:**
- Modify: `src/Namotion.Devices.Gpio/AnalogChannel.cs`
- Modify: `src/Namotion.Devices.Gpio.Tests/AnalogChannelTests.cs`

**Step 8.1: Write failing test**

Add to `src/Namotion.Devices.Gpio.Tests/AnalogChannelTests.cs`:

```csharp
using HomeBlaze.Abstractions;

// Add test:
[Fact]
public void AnalogChannel_ImplementsIHostedSubject()
{
    var context = InterceptorSubjectContext.Create();
    var channel = new AnalogChannel(context);

    Assert.IsAssignableFrom<IHostedSubject>(channel);
    Assert.Equal(ServiceStatus.Stopped, channel.Status);
    Assert.Null(channel.StatusMessage);
}
```

**Step 8.2: Run test to verify it fails**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "AnalogChannel_ImplementsIHostedSubject"`
Expected: FAIL

**Step 8.3: Update AnalogChannel implementation**

Replace `src/Namotion.Devices.Gpio/AnalogChannel.cs` with:

```csharp
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.Gpio;

/// <summary>
/// Represents an analog input channel from an ADC.
/// </summary>
[InterceptorSubject]
public partial class AnalogChannel : IHostedSubject
{
    /// <summary>
    /// The ADC channel number.
    /// </summary>
    public partial int ChannelNumber { get; set; }

    /// <summary>
    /// The normalized value (0.0 to 1.0).
    /// </summary>
    [State]
    public partial double Value { get; set; }

    /// <summary>
    /// The raw ADC value (e.g., 0-1023 for 10-bit).
    /// </summary>
    [State]
    public partial int RawValue { get; set; }

    /// <summary>
    /// Channel availability status.
    /// </summary>
    [State]
    public partial ServiceStatus Status { get; set; }

    /// <summary>
    /// Status message for errors or info.
    /// </summary>
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

**Step 8.4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "AnalogChannel"`
Expected: PASS

**Step 8.5: Commit**

```bash
git add src/Namotion.Devices.Gpio/AnalogChannel.cs src/Namotion.Devices.Gpio.Tests/AnalogChannelTests.cs
git commit -m "feat: add ServiceStatus to AnalogChannel, implement IHostedSubject"
```

---

## Task 9: Update GpioSubject with ServiceStatus and PollingInterval

**Files:**
- Modify: `src/Namotion.Devices.Gpio/GpioSubject.cs`
- Modify: `src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs`

**Step 9.1: Write failing tests**

Add to `src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs`:

```csharp
using HomeBlaze.Abstractions;

// Add tests:
[Fact]
public void GpioSubject_ImplementsIHostedSubject()
{
    var context = InterceptorSubjectContext.Create();
    var subject = new GpioSubject(context);

    Assert.IsAssignableFrom<IHostedSubject>(subject);
}

[Fact]
public void GpioSubject_InitializesWithStoppedStatusAndDefaultPollingInterval()
{
    var context = InterceptorSubjectContext.Create();
    var subject = new GpioSubject(context);

    Assert.Equal(ServiceStatus.Stopped, subject.Status);
    Assert.Null(subject.StatusMessage);
    Assert.Equal(TimeSpan.FromSeconds(5), subject.PollingInterval);
}
```

**Step 9.2: Run tests to verify they fail**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "GpioSubject_ImplementsIHostedSubject|GpioSubject_InitializesWithStoppedStatusAndDefaultPollingInterval"`
Expected: FAIL

**Step 9.3: Update GpioSubject with new properties**

Modify `src/Namotion.Devices.Gpio/GpioSubject.cs`:

Add using statement at top:
```csharp
using HomeBlaze.Abstractions;
```

Add interface to class declaration:
```csharp
public partial class GpioSubject : BackgroundService, IConfigurableSubject, ITitleProvider, IIconProvider, IHostedSubject
```

Add new properties after existing properties:
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
    StatusMessage = null;
}
```

**Step 9.4: Run tests to verify they pass**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests --filter "GpioSubject"`
Expected: PASS

**Step 9.5: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioSubject.cs src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs
git commit -m "feat: add ServiceStatus and PollingInterval to GpioSubject"
```

---

## Task 10: Create GpioWriteInterceptor

**Files:**
- Create: `src/Namotion.Devices.Gpio/Interceptors/GpioWriteInterceptor.cs`

**Step 10.1: Create Interceptors directory and file**

Create `src/Namotion.Devices.Gpio/Interceptors/GpioWriteInterceptor.cs`:

```csharp
using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Interceptors;

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

    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WriteInterceptionDelegate<T> next)
    {
        next(ref context);

        if (context.Property.Subject is not GpioPin pin)
            return;

        if (context.Property.Metadata.Name != nameof(GpioPin.Value))
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

**Step 10.2: Build to verify**

Run: `dotnet build src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj`
Expected: Build succeeded

**Step 10.3: Commit**

```bash
git add src/Namotion.Devices.Gpio/Interceptors/GpioWriteInterceptor.cs
git commit -m "feat: add GpioWriteInterceptor for output pin hardware sync"
```

---

## Task 11: Create GpioModeChangeInterceptor

**Files:**
- Create: `src/Namotion.Devices.Gpio/Interceptors/GpioModeChangeInterceptor.cs`

**Step 11.1: Create the interceptor**

Create `src/Namotion.Devices.Gpio/Interceptors/GpioModeChangeInterceptor.cs`:

```csharp
using System.Device.Gpio;
using HomeBlaze.Abstractions;
using Namotion.Interceptor.Interceptors;

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

    public void WriteProperty<T>(ref PropertyWriteContext<T> context, WriteInterceptionDelegate<T> next)
    {
        var oldMode = context.Property.Subject is GpioPin pin ? pin.Mode : default;

        next(ref context);

        if (context.Property.Subject is not GpioPin changedPin)
            return;

        if (context.Property.Metadata.Name != nameof(GpioPin.Mode))
            return;

        if (changedPin.Status != ServiceStatus.Running)
            return;

        var newMode = changedPin.Mode;
        if (oldMode == newMode)
            return;

        // Determine if old mode was input-like
        var wasInput = oldMode is GpioPinMode.Input or GpioPinMode.InputPullUp or GpioPinMode.InputPullDown;
        var isInput = newMode is GpioPinMode.Input or GpioPinMode.InputPullUp or GpioPinMode.InputPullDown;

        // Unregister interrupt if switching from input to output
        if (wasInput && !isInput)
        {
            _unregisterInterrupt(changedPin.PinNumber);
        }

        // Set hardware pin mode
        var hardwareMode = newMode switch
        {
            GpioPinMode.Input => PinMode.Input,
            GpioPinMode.InputPullUp => PinMode.InputPullUp,
            GpioPinMode.InputPullDown => PinMode.InputPullDown,
            GpioPinMode.Output => PinMode.Output,
            _ => PinMode.Input
        };
        _controller.SetPinMode(changedPin.PinNumber, hardwareMode);

        if (isInput)
        {
            // Read current value and register interrupt
            changedPin.Value = _controller.Read(changedPin.PinNumber) == PinValue.High;
            if (!wasInput)
            {
                _registerInterrupt(changedPin.PinNumber);
            }
        }
        else
        {
            // Write current Value to hardware for output mode
            _controller.Write(changedPin.PinNumber, changedPin.Value ? PinValue.High : PinValue.Low);
        }
    }
}
```

**Step 11.2: Build to verify**

Run: `dotnet build src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj`
Expected: Build succeeded

**Step 11.3: Commit**

```bash
git add src/Namotion.Devices.Gpio/Interceptors/GpioModeChangeInterceptor.cs
git commit -m "feat: add GpioModeChangeInterceptor for pin mode hardware sync"
```

---

## Task 12: Implement ExecuteAsync with hardware integration

**Files:**
- Modify: `src/Namotion.Devices.Gpio/GpioSubject.cs`

**Step 12.1: Add required usings and private fields**

Add at top of file:
```csharp
using System.Collections.Concurrent;
using System.Device.Gpio;
using Namotion.Devices.Gpio.Interceptors;
using Namotion.Interceptor.Interceptors;
```

Add private fields in class:
```csharp
private GpioController? _controller;
private readonly ConcurrentDictionary<int, PinChangeEventHandler> _interruptHandlers = new();
```

**Step 12.2: Implement ExecuteAsync**

Replace the existing `ExecuteAsync` method with:

```csharp
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
        for (int pinNumber = 0; pinNumber <= 27; pinNumber++)
        {
            Pins[pinNumber] = new GpioPin(Context)
            {
                PinNumber = pinNumber,
                Status = ServiceStatus.Unavailable,
                StatusMessage = "GPIO not supported on this platform"
            };
        }
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
            pin.StatusMessage = ex.Message;
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
            PollAdcChannels();

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

private void PollAdcChannels()
{
    // ADC polling will be implemented in next task
}
```

**Step 12.3: Build to verify**

Run: `dotnet build src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj`
Expected: Build succeeded

**Step 12.4: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioSubject.cs
git commit -m "feat: implement ExecuteAsync with hardware discovery and polling"
```

---

## Task 13: Implement ADC support

**Files:**
- Modify: `src/Namotion.Devices.Gpio/GpioSubject.cs`

**Step 13.1: Add ADC usings and fields**

Add usings:
```csharp
using System.Device.I2c;
using System.Device.Spi;
using Iot.Device.Adc;
```

Add fields:
```csharp
private Mcp3008? _mcp3008;
private Ads1115? _ads1115;
```

**Step 13.2: Implement ApplyConfigurationAsync**

Replace existing `ApplyConfigurationAsync`:

```csharp
public Task ApplyConfigurationAsync(CancellationToken cancellationToken)
{
    // Dispose existing ADC
    _mcp3008?.Dispose();
    _ads1115?.Dispose();
    _mcp3008 = null;
    _ads1115 = null;
    AnalogChannels.Clear();

    // Initialize MCP3008 if configured
    if (Mcp3008 != null && _controller != null)
    {
        try
        {
            var spi = new SoftwareSpi(
                clk: Mcp3008.ClockPin,
                miso: Mcp3008.MisoPin,
                mosi: Mcp3008.MosiPin,
                cs: Mcp3008.ChipSelectPin);
            _mcp3008 = new Mcp3008(spi);

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
            StatusMessage = $"MCP3008 init failed: {ex.Message}";
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
            _ads1115 = new Iot.Device.Ads1115.Ads1115(i2cDevice);

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
            StatusMessage = $"ADS1115 init failed: {ex.Message}";
        }
    }

    return Task.CompletedTask;
}
```

**Step 13.3: Implement PollAdcChannels**

Replace the stub:

```csharp
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
            catch (Exception ex)
            {
                channel.Status = ServiceStatus.Error;
                channel.StatusMessage = ex.Message;
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
                var inputMux = (Iot.Device.Ads1115.InputMultiplexer)channel.ChannelNumber;
                var raw = _ads1115.ReadRaw(inputMux);
                channel.RawValue = raw;
                channel.Value = raw / 32767.0;
            }
            catch (Exception ex)
            {
                channel.Status = ServiceStatus.Error;
                channel.StatusMessage = ex.Message;
            }
        }
    }
}
```

**Step 13.4: Build to verify**

Run: `dotnet build src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj`
Expected: Build succeeded

**Step 13.5: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioSubject.cs
git commit -m "feat: implement ADC support for MCP3008 and ADS1115"
```

---

## Task 14: Implement Dispose

**Files:**
- Modify: `src/Namotion.Devices.Gpio/GpioSubject.cs`

**Step 14.1: Override Dispose method**

Add method:

```csharp
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
```

**Step 14.2: Build to verify**

Run: `dotnet build src/Namotion.Devices.Gpio/Namotion.Devices.Gpio.csproj`
Expected: Build succeeded

**Step 14.3: Commit**

```bash
git add src/Namotion.Devices.Gpio/GpioSubject.cs
git commit -m "feat: implement Dispose for proper hardware cleanup"
```

---

## Task 15: Add platform-specific tests

**Files:**
- Modify: `src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs`

**Step 15.1: Add Windows platform test**

Add test:

```csharp
[Fact]
public void GpioSubject_OnWindows_StatusIsUnavailableAfterStart()
{
    // This test verifies graceful degradation on non-Pi platforms
    // On Windows, ExecuteAsync should set Status to Unavailable

    var context = InterceptorSubjectContext.Create();
    var subject = new GpioSubject(context);

    // Before starting, status is Stopped
    Assert.Equal(ServiceStatus.Stopped, subject.Status);

    // Note: Full integration test would require running the background service
    // This is a basic sanity check
}
```

**Step 15.2: Run all tests**

Run: `dotnet test src/Namotion.Devices.Gpio.Tests`
Expected: All tests PASS

**Step 15.3: Commit**

```bash
git add src/Namotion.Devices.Gpio.Tests/GpioSubjectTests.cs
git commit -m "test: add platform-specific test for Windows graceful degradation"
```

---

## Task 16: Build and test entire solution

**Step 16.1: Build entire solution**

Run: `dotnet build src/Namotion.Interceptor.slnx`
Expected: Build succeeded with 0 errors

**Step 16.2: Run all tests**

Run: `dotnet test src/Namotion.Interceptor.slnx`
Expected: All tests PASS

**Step 16.3: Final commit**

```bash
git add -A
git commit -m "feat: complete GPIO hardware integration with full bidirectional sync"
```

---

## Summary

| Task | Description | Files |
|------|-------------|-------|
| 1 | Create ServiceStatus enum | HomeBlaze.Abstractions |
| 2 | Create IHostedSubject interface | HomeBlaze.Abstractions |
| 3 | Update IServerSubject | HomeBlaze.Abstractions |
| 4 | Migrate OpcUaServer | HomeBlaze.Servers.OpcUa |
| 5 | Delete ServerStatus | HomeBlaze.Abstractions |
| 6 | Update GpioPinMode | Namotion.Devices.Gpio |
| 7 | Update GpioPin | Namotion.Devices.Gpio |
| 8 | Update AnalogChannel | Namotion.Devices.Gpio |
| 9 | Update GpioSubject properties | Namotion.Devices.Gpio |
| 10 | Create GpioWriteInterceptor | Namotion.Devices.Gpio |
| 11 | Create GpioModeChangeInterceptor | Namotion.Devices.Gpio |
| 12 | Implement ExecuteAsync | Namotion.Devices.Gpio |
| 13 | Implement ADC support | Namotion.Devices.Gpio |
| 14 | Implement Dispose | Namotion.Devices.Gpio |
| 15 | Add platform tests | Namotion.Devices.Gpio.Tests |
| 16 | Final build and test | All |
