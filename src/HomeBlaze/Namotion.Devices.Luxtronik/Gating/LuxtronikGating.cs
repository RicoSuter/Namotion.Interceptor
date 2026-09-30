using Namotion.Devices.Luxtronik.Enums;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.Luxtronik.Gating;

/// <summary>
/// Decides whether a register is read, from the controller firmware and its active functions. Active functions are a
/// bit mask indexed by <see cref="LuxtronikFunction"/>; <c>null</c> means the controller does not report them.
/// </summary>
internal static class LuxtronikGating
{
    public const int FunctionFlagCount = 12;

    /// <summary>
    /// Gets firmware version 3.92.0, the gate of the registers added in that release.
    /// </summary>
    public static readonly Version Firmware392 = new(3, 92, 0);

    /// <summary>
    /// Gets whether <paramref name="property"/> is read: its register gates and its subject's gate must all pass.
    /// On an <see cref="ILuxtronikCircuitSubject"/>, register function gates are shifted to the circuit's own function.
    /// </summary>
    public static bool IsSupported(RegisteredSubjectProperty property, Version firmwareVersion, int? functionMask)
    {
        var functionOffset = property.Subject is ILuxtronikCircuitSubject circuit ? circuit.FunctionOffset : 0;
        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ILuxtronikRegisterGate gate &&
                !IsSupported(gate.MinimumFirmwareVersion, Shift(gate.Function, functionOffset), firmwareVersion, functionMask))
            {
                return false;
            }
        }

        return property.Subject is not ILuxtronikGatedSubject subject ||
            IsSupported(subject.MinimumFirmwareVersion, subject.Function, firmwareVersion, functionMask);
    }

    /// <summary>
    /// Gets whether a gate passes: the firmware is at least <paramref name="minimumFirmwareVersion"/>, and
    /// <paramref name="function"/> is <see cref="LuxtronikFunction.None"/> or active.
    /// </summary>
    public static bool IsSupported(
        Version? minimumFirmwareVersion,
        LuxtronikFunction function,
        Version firmwareVersion,
        int? functionMask)
    {
        if (minimumFirmwareVersion is not null && firmwareVersion < minimumFirmwareVersion)
        {
            return false;
        }

        return function == LuxtronikFunction.None || IsActive(functionMask, function);
    }

    /// <summary>
    /// Gets whether <paramref name="function"/> is active in <paramref name="functionMask"/>. Every function counts as
    /// active while the mask is unknown (<c>null</c>); otherwise <see cref="LuxtronikFunction.None"/> never is.
    /// </summary>
    public static bool IsActive(int? functionMask, LuxtronikFunction function)
        => functionMask is not { } mask ||
            (function != LuxtronikFunction.None && (mask & (1 << (int)function)) != 0);

    /// <summary>
    /// Gets whether <paramref name="property"/> is a mapped register that can hold <c>null</c>.
    /// </summary>
    public static bool IsNullableRegister(RegisteredSubjectProperty property)
    {
        if (property.Type.IsValueType && Nullable.GetUnderlyingType(property.Type) is null)
        {
            return false;
        }

        foreach (var attribute in property.ReflectionAttributes)
        {
            if (attribute is ModbusRegisterAttribute)
            {
                return true;
            }
        }

        return false;
    }

    private static LuxtronikFunction Shift(LuxtronikFunction function, int offset)
        => function == LuxtronikFunction.None ? function : function + offset;

    /// <summary>
    /// Gets the set flags as a bit mask; the bit index is the <see cref="LuxtronikFunction"/> value.
    /// </summary>
    public static int GetFunctionMask(ReadOnlySpan<bool> flags)
    {
        var mask = 0;
        for (var index = 0; index < Math.Min(flags.Length, FunctionFlagCount); index++)
        {
            if (flags[index])
            {
                mask |= 1 << index;
            }
        }

        return mask;
    }

    /// <summary>
    /// Gets the names of the functions whose bit is set in <paramref name="mask"/>, separated by commas.
    /// </summary>
    public static string GetFunctionNames(int mask)
    {
        var names = new List<string>();
        for (var index = 0; index < FunctionFlagCount; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                names.Add(((LuxtronikFunction)index).ToString());
            }
        }

        return string.Join(", ", names);
    }
}
