using System.Device.Gpio;

namespace Namotion.Devices.Gpio.Tests.Mocks;

/// <summary>
/// Mock GPIO driver for testing GPIO functionality without real hardware.
/// </summary>
public class MockGpioDriver : GpioDriver
{
    private readonly Dictionary<int, PinValue> _pinValues = new();
    private readonly Dictionary<int, PinMode> _pinModes = new();
    private readonly HashSet<int> _openPins = new();
    private readonly Dictionary<int, List<PinChangeEventHandler>> _eventHandlers = new();

    /// <summary>
    /// Gets the current pin values for verification.
    /// </summary>
    public IReadOnlyDictionary<int, PinValue> PinValues => _pinValues;

    /// <summary>
    /// Gets the current pin modes for verification.
    /// </summary>
    public IReadOnlyDictionary<int, PinMode> PinModes => _pinModes;

    /// <summary>
    /// Gets the set of open pins for verification.
    /// </summary>
    public IReadOnlySet<int> OpenPins => _openPins;

    /// <summary>
    /// Gets write call history for verification.
    /// </summary>
    public List<(int PinNumber, PinValue Value)> WriteHistory { get; } = new();

    /// <summary>
    /// Gets read call history for verification.
    /// </summary>
    public List<int> ReadHistory { get; } = new();

    /// <summary>
    /// Simulates a pin value change (as if hardware changed).
    /// </summary>
    public void SimulatePinValueChange(int pinNumber, PinValue newValue)
    {
        var oldValue = _pinValues.GetValueOrDefault(pinNumber, PinValue.Low);
        _pinValues[pinNumber] = newValue;

        if (_eventHandlers.TryGetValue(pinNumber, out var handlers))
        {
            var changeType = newValue == PinValue.High ? PinEventTypes.Rising : PinEventTypes.Falling;
            var args = new PinValueChangedEventArgs(changeType, pinNumber);
            foreach (var handler in handlers.ToList())
            {
                handler(this, args);
            }
        }
    }

    protected override int PinCount => 28; // BCM pins 0-27

    protected override int ConvertPinNumberToLogicalNumberingScheme(int pinNumber) => pinNumber;

    protected override void OpenPin(int pinNumber)
    {
        _openPins.Add(pinNumber);
        if (!_pinValues.ContainsKey(pinNumber))
        {
            _pinValues[pinNumber] = PinValue.Low;
        }
        if (!_pinModes.ContainsKey(pinNumber))
        {
            _pinModes[pinNumber] = PinMode.Input;
        }
    }

    protected override void ClosePin(int pinNumber)
    {
        _openPins.Remove(pinNumber);
    }

    protected override void SetPinMode(int pinNumber, PinMode mode)
    {
        _pinModes[pinNumber] = mode;
    }

    protected override PinMode GetPinMode(int pinNumber)
    {
        return _pinModes.GetValueOrDefault(pinNumber, PinMode.Input);
    }

    protected override bool IsPinModeSupported(int pinNumber, PinMode mode) => true;

    protected override PinValue Read(int pinNumber)
    {
        ReadHistory.Add(pinNumber);
        return _pinValues.GetValueOrDefault(pinNumber, PinValue.Low);
    }

    protected override void Write(int pinNumber, PinValue value)
    {
        WriteHistory.Add((pinNumber, value));
        _pinValues[pinNumber] = value;
    }

    protected override void AddCallbackForPinValueChangedEvent(int pinNumber, PinEventTypes eventTypes, PinChangeEventHandler callback)
    {
        if (!_eventHandlers.ContainsKey(pinNumber))
        {
            _eventHandlers[pinNumber] = new List<PinChangeEventHandler>();
        }
        _eventHandlers[pinNumber].Add(callback);
    }

    protected override void RemoveCallbackForPinValueChangedEvent(int pinNumber, PinChangeEventHandler callback)
    {
        if (_eventHandlers.TryGetValue(pinNumber, out var handlers))
        {
            handlers.Remove(callback);
        }
    }

    protected override WaitForEventResult WaitForEvent(int pinNumber, PinEventTypes eventTypes, CancellationToken cancellationToken)
    {
        return new WaitForEventResult { EventTypes = PinEventTypes.None, TimedOut = true };
    }
}
