using Microsoft.Extensions.Logging;
using Namotion.Devices.SunSpec.Models;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;

namespace Namotion.Devices.SunSpec.Discovery;

/// <summary>
/// The discovery of a <see cref="SunSpecDevice"/>, run by the Modbus source on every connect.
/// </summary>
internal static class SunSpecDiscovery
{
    /// <summary>
    /// Discovers every unit, keeping the subjects of units and models that did not change. A unit without a marker or
    /// with a malformed chain is logged and left out.
    /// </summary>
    public static async Task<Dictionary<int, SunSpecUnit>> DiscoverAsync(
        Dictionary<int, SunSpecUnit> currentUnits, IReadOnlyList<byte> unitIds, ModbusDiscoveryContext context,
        SunSpecModelCatalog catalog, bool isRegisterDumpEnabled, ILogger logger, CancellationToken cancellationToken)
    {
        var units = new Dictionary<int, SunSpecUnit>();
        foreach (var unitId in unitIds)
        {
            var unit = currentUnits.GetValueOrDefault(unitId) ?? new SunSpecUnit(unitId);
            if (await DiscoverUnitAsync(unit, context, catalog, isRegisterDumpEnabled, logger, cancellationToken).ConfigureAwait(false))
            {
                units[unitId] = unit;
            }
        }

        return units;
    }

    /// <summary>
    /// Gets whether both dictionaries hold the same unit subjects under the same unit IDs.
    /// </summary>
    public static bool HaveSameUnits(Dictionary<int, SunSpecUnit> current, Dictionary<int, SunSpecUnit> next)
        => current.Count == next.Count && next.All(pair => current.TryGetValue(pair.Key, out var unit) && ReferenceEquals(unit, pair.Value));

    /// <summary>
    /// Adds the properties of dynamic models, which needs them attached, and excludes the registers that lie beyond the
    /// length a device reports for a model, so they are not read from the next model.
    /// </summary>
    public static void CompleteModels(IEnumerable<SunSpecUnit> units, ModbusDiscoveryContext context)
    {
        foreach (var model in units.SelectMany(unit => unit.Devices).SelectMany(device => device.GetModels()))
        {
            (model as SunSpecDynamicModel)?.EnsureProperties();
            ExcludeRegistersBeyondLength(model, context);
        }
    }

    private static async Task<bool> DiscoverUnitAsync(
        SunSpecUnit unit, ModbusDiscoveryContext context, SunSpecModelCatalog catalog, bool isRegisterDumpEnabled, ILogger logger, CancellationToken cancellationToken)
    {
        SunSpecChain? chain;
        try
        {
            chain = await SunSpecChainReader.ReadAsync(
                (address, count, token) => ReadAsync(context, unit.UnitId, address, count, token), cancellationToken).ConfigureAwait(false);

            if (chain is null)
            {
                logger.LogWarning("SunSpec unit {UnitId} has no \"SunS\" marker at 40000, 50000 or 0, so it is skipped.", unit.UnitId);
                return false;
            }

            var devices = SunSpecUnitBuilder.Build(unit.UnitId, chain, unit.Devices, catalog);
            unit.MarkerAddress = chain.MarkerAddress;
            unit.Devices = devices;
        }
        catch (InvalidDataException exception)
        {
            logger.LogError(exception, "SunSpec unit {UnitId} has a malformed model chain, so it is skipped.", unit.UnitId);
            return false;
        }

        LogChain(unit, chain, logger);
        if (isRegisterDumpEnabled)
        {
            logger.LogInformation("SunSpec register dump of unit {UnitId}: {Dump}", unit.UnitId, SunSpecRegisterDump.Write(unit.UnitId, chain));
        }

        return true;
    }

    // A permanent rejection means the registers do not exist, which the chain reader handles; anything else, such as a
    // lost connection, fails the connect attempt.
    private static async Task<ushort[]?> ReadAsync(ModbusDiscoveryContext context, byte unitId, int address, int count, CancellationToken cancellationToken)
    {
        try
        {
            return await context.ReadHoldingRegistersAsync(address, count, unitId, cancellationToken).ConfigureAwait(false);
        }
        catch (ModbusResponseException exception) when (exception.IsPermanentRejection)
        {
            return null;
        }
    }

    private static void LogChain(SunSpecUnit unit, SunSpecChain chain, ILogger logger)
    {
        var description = string.Join(",", chain.Models.Select(model => $"{model.ModelId}@{model.Address}+{model.Length}"));
        if (description == unit.ChainDescription)
        {
            return;
        }

        unit.ChainDescription = description;
        logger.LogInformation("SunSpec unit {UnitId} has its model chain at {MarkerAddress}.", unit.UnitId, chain.MarkerAddress);
        foreach (var model in unit.Devices.SelectMany(device => device.GetModels()))
        {
            logger.LogInformation(
                "SunSpec unit {UnitId}: model {ModelId} at {Address}, length {Length}, read as {ModelType}.",
                unit.UnitId, model.ModelId, model.BaseAddress, model.Length, model.GetType().Name);
        }
    }

    private static void ExcludeRegistersBeyondLength(ISunSpecModel model, ModbusDiscoveryContext context)
    {
        if (model.TryGetRegisteredSubject() is not { } registered)
        {
            return;
        }

        // Offsets count from the ID register, so the points of a model end at offset Length + 2.
        var end = model.Length + 2;
        foreach (var property in registered.Properties)
        {
            var attribute = property.ReflectionAttributes.OfType<ModbusRegisterAttribute>().FirstOrDefault();
            if (attribute is not null && attribute.Address + GetRegisterCount(attribute) > end)
            {
                context.ExcludeProperty(property.Reference);
            }
        }
    }

    private static int GetRegisterCount(ModbusRegisterAttribute attribute) => attribute.DataType switch
    {
        ModbusDataType.String => attribute.Length,
        ModbusDataType.U64 or ModbusDataType.S64 => 4,
        ModbusDataType.U32 or ModbusDataType.S32 or ModbusDataType.F32 => 2,
        _ => 1
    };
}
