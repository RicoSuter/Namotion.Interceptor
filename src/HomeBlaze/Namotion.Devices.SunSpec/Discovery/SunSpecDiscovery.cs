using Microsoft.Extensions.Logging;
using Namotion.Devices.SunSpec.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Client;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Change;

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
    /// Gets the status message of a connected device: which configured units were not found, or <c>null</c> when all were.
    /// </summary>
    public static string? GetStatusMessage(IReadOnlyList<byte> unitIds, Dictionary<int, SunSpecUnit> units)
    {
        if (units.Count == 0)
        {
            return "No SunSpec unit found";
        }

        var missingUnitIds = unitIds.Where(unitId => !units.ContainsKey(unitId)).ToArray();
        return missingUnitIds.Length switch
        {
            0 => null,
            1 => $"Unit {missingUnitIds[0]} not found",
            _ => $"Units {string.Join(", ", missingUnitIds)} not found"
        };
    }

    /// <summary>
    /// Gets whether both dictionaries hold the same unit subjects under the same unit IDs.
    /// </summary>
    public static bool HaveSameUnits(Dictionary<int, SunSpecUnit> current, Dictionary<int, SunSpecUnit> next)
        => current.Count == next.Count && next.All(pair => current.TryGetValue(pair.Key, out var unit) && ReferenceEquals(unit, pair.Value));

    /// <summary>
    /// Adds the properties of dynamic models, which needs them attached, excludes the properties a device does not
    /// provide (see <see cref="GetUnavailableProperties"/>), and applies each discovered model ID to the model's ID
    /// register.
    /// </summary>
    public static void CompleteModels(IEnumerable<SunSpecUnit> units, ModbusDiscoveryContext context)
    {
        foreach (var model in units.SelectMany(unit => unit.Devices).SelectMany(device => device.GetModels()))
        {
            (model as SunSpecDynamicModel)?.EnsureProperties();
            foreach (var property in GetUnavailableProperties(model))
            {
                context.ExcludeProperty(property);
            }

            ApplyDiscoveredModelId(model, context.Source);
        }
    }

    /// <summary>
    /// Gets the register properties a device does not provide for a model: those beyond the length it reports, which
    /// would otherwise be read from the next model, and those scaled by an unavailable scale factor, including the
    /// points of nested groups.
    /// </summary>
    internal static IReadOnlyCollection<PropertyReference> GetUnavailableProperties(ISunSpecModel model)
    {
        if (model.TryGetRegisteredSubject() is not { } registered)
        {
            return [];
        }

        // Offsets count from the ID register, so the points of a model end at offset Length + 2.
        var end = model.Length + 2;
        var unavailable = new HashSet<PropertyReference>(PropertyReference.Comparer);
        foreach (var property in registered.Properties)
        {
            if (GetRegisterAttribute(property) is { } attribute && attribute.Address + GetRegisterCount(attribute) > end)
            {
                unavailable.Add(property.Reference);
            }
        }

        if (unavailable.Count > 0)
        {
            AddScaledProperties(registered, unavailable);
        }

        return unavailable;
    }

    /// <summary>
    /// Sets the model ID register to the ID the discovery read, replacing a value polled by a previous source that
    /// would otherwise look like a chain change.
    /// </summary>
    internal static void ApplyDiscoveredModelId(ISunSpecModel model, object source)
        => new PropertyReference(model, nameof(ISunSpecModel.ModelIdRegister)).SetValueFromSource(source, null, null, (ushort?)model.ModelId);

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

    // The connector rejects a mapping whose scale factor is excluded, so a property scaled by an unavailable one is
    // unavailable too; repeated until stable because a newly unavailable property may scale others.
    private static void AddScaledProperties(RegisteredSubject model, HashSet<PropertyReference> unavailable)
    {
        var scaledProperties = new List<(PropertyReference Property, PropertyReference ScaleFactor)>();
        CollectScaledProperties(model, scaledProperties, []);

        bool hasChanged;
        do
        {
            hasChanged = false;
            foreach (var (property, scaleFactor) in scaledProperties)
            {
                if (unavailable.Contains(scaleFactor) && unavailable.Add(property))
                {
                    hasChanged = true;
                }
            }
        }
        while (hasChanged);
    }

    private static void CollectScaledProperties(
        RegisteredSubject subject, List<(PropertyReference Property, PropertyReference ScaleFactor)> scaledProperties, HashSet<RegisteredSubject> visited)
    {
        if (!visited.Add(subject))
        {
            return;
        }

        foreach (var property in subject.Properties)
        {
            if (GetRegisterAttribute(property) is { } attribute && GetScaleFactor(property, attribute) is { } scaleFactor)
            {
                scaledProperties.Add((property.Reference, scaleFactor));
            }

            foreach (var child in property.Children)
            {
                if (child.Subject.TryGetRegisteredSubject() is { } registeredChild)
                {
                    CollectScaledProperties(registeredChild, scaledProperties, visited);
                }
            }
        }
    }

    // Resolved like the connector resolves it: the subject's provider first, then the attribute.
    private static PropertyReference? GetScaleFactor(RegisteredSubjectProperty property, ModbusRegisterAttribute attribute)
    {
        var subject = property.Reference.Subject;
        if (subject is IModbusScaleFactorProvider provider && provider.TryGetScaleFactorProperty(property.Name) is { } provided)
        {
            return provided;
        }

        return attribute.ScaleFactorProperty is { } name ? new PropertyReference(subject, name) : null;
    }

    private static ModbusRegisterAttribute? GetRegisterAttribute(RegisteredSubjectProperty property)
        => property.ReflectionAttributes.OfType<ModbusRegisterAttribute>().FirstOrDefault();

    private static int GetRegisterCount(ModbusRegisterAttribute attribute) => attribute.DataType switch
    {
        ModbusDataType.String => attribute.Length,
        ModbusDataType.U64 or ModbusDataType.S64 => 4,
        ModbusDataType.U32 or ModbusDataType.S32 or ModbusDataType.F32 => 2,
        _ => 1
    };
}
