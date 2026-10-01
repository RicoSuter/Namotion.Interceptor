using Microsoft.Extensions.Logging;
using Namotion.Devices.Luxtronik.Enums;
using Namotion.Devices.Luxtronik.Gating;
using Namotion.Devices.Luxtronik.Model;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Devices.Luxtronik;

/// <summary>
/// The discovery of a <see cref="LuxtronikHeatPump"/>, run by the Modbus source on every connect.
/// </summary>
internal static class LuxtronikDiscovery
{
    private const int FirmwareAddress = 10400;

    /// <summary>
    /// Reads the firmware version and active functions, creates or removes the optional function subjects, and excludes
    /// the registers the controller does not provide.
    /// </summary>
    /// <returns>The active functions as a mask, or <c>null</c> when the controller does not report them.</returns>
    public static async Task<int?> DiscoverAsync(
        LuxtronikHeatPump heatPump, ModbusDiscoveryContext context, ILogger logger, CancellationToken cancellationToken)
    {
        var versionRegisters = await context.ReadInputRegistersAsync(FirmwareAddress, 3, cancellationToken: cancellationToken).ConfigureAwait(false);
        var firmwareVersion = new Version(versionRegisters[0], versionRegisters[1], versionRegisters[2]);
        new PropertyReference(heatPump, nameof(LuxtronikHeatPump.SoftwareVersion))
            .SetValueFromSource(context.Source, null, null, firmwareVersion.ToString());

        int? functionMask = null;
        try
        {
            var flags = await context.ReadDiscreteInputsAsync(heatPump.Functions.BaseAddress, LuxtronikGating.FunctionFlagCount, cancellationToken: cancellationToken).ConfigureAwait(false);
            functionMask = LuxtronikGating.GetFunctionMask(flags);

            // Keeps the polled flags in step with the discovered ones even while the initial load fails, since a
            // mismatch makes the status loop restart the source.
            heatPump.Functions.SetFromSource(context.Source, functionMask.Value);
        }
        catch (ModbusResponseException exception) when (exception.IsPermanentRejection)
        {
            logger.LogInformation(
                exception,
                "Luxtronik {HostAddress} does not report which functions are active (exception code {ExceptionCode}); values of inactive functions are shown as unavailable instead.",
                heatPump.HostAddress, exception.ExceptionCode);
        }

        UpdateFunctionSubjects(heatPump, functionMask);

        var registeredSubject = heatPump.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("The heat pump is not registered. Attach it to a subject graph with a registry.");

        foreach (var property in registeredSubject.GetAllProperties())
        {
            var isUnreadableFunctionFlag = functionMask is null && ReferenceEquals(property.Subject, heatPump.Functions);
            if (isUnreadableFunctionFlag || !LuxtronikGating.IsSupported(property, firmwareVersion, functionMask))
            {
                context.ExcludeProperty(property.Reference);

                // A register of a kept subject (a mixing circuit losing one of its flags) would otherwise keep its stale reading.
                if (LuxtronikGating.IsNullableRegister(property))
                {
                    property.Reference.SetValueFromSource(context.Source, null, null, null);
                }
            }
        }

        logger.LogInformation("Luxtronik {HostAddress} runs firmware {FirmwareVersion}.", heatPump.HostAddress, firmwareVersion);
        return functionMask;
    }

    /// <summary>
    /// Creates the subjects of the active optional functions, keeps existing ones, and removes the inactive ones; all
    /// exist when <paramref name="functionMask"/> is unknown (<c>null</c>).
    /// </summary>
    public static void UpdateFunctionSubjects(LuxtronikHeatPump heatPump, int? functionMask)
    {
        heatPump.Cooling = GetFunctionSubject(functionMask, LuxtronikFunction.Cooling, LuxtronikFunction.None,
            heatPump.Cooling, static () => new LuxtronikCooling());
        heatPump.Pool = GetFunctionSubject(functionMask, LuxtronikFunction.Pool, LuxtronikFunction.None,
            heatPump.Pool, static () => new LuxtronikPool());
        heatPump.Solar = GetFunctionSubject(functionMask, LuxtronikFunction.Solar, LuxtronikFunction.None,
            heatPump.Solar, static () => new LuxtronikSolar());
        heatPump.RoomControl = GetFunctionSubject(functionMask, LuxtronikFunction.RoomControlUnit, LuxtronikFunction.None,
            heatPump.RoomControl, static () => new LuxtronikRoomControl());
        heatPump.MixingCircuit1 = GetFunctionSubject(functionMask, LuxtronikFunction.MixingCircuit1Heating, LuxtronikFunction.MixingCircuit1Cooling,
            heatPump.MixingCircuit1, static () => new LuxtronikMixingCircuit(1));
        heatPump.MixingCircuit2 = GetFunctionSubject(functionMask, LuxtronikFunction.MixingCircuit2Heating, LuxtronikFunction.MixingCircuit2Cooling,
            heatPump.MixingCircuit2, static () => new LuxtronikMixingCircuit(2));
        heatPump.MixingCircuit3 = GetFunctionSubject(functionMask, LuxtronikFunction.MixingCircuit3Heating, LuxtronikFunction.MixingCircuit3Cooling,
            heatPump.MixingCircuit3, static () => new LuxtronikMixingCircuit(3));
    }

    // LuxtronikFunction.None is never active, so it serves as "no alternative".
    private static TSubject? GetFunctionSubject<TSubject>(
        int? functionMask,
        LuxtronikFunction function,
        LuxtronikFunction alternativeFunction,
        TSubject? current,
        Func<TSubject> create)
        where TSubject : class
    {
        var isActive = LuxtronikGating.IsActive(functionMask, function) || LuxtronikGating.IsActive(functionMask, alternativeFunction);
        return isActive ? current ?? create() : null;
    }
}
