using System.Text.Json;

namespace Namotion.Devices.Shelly;

/// <summary>
/// A virtual number component read from Shelly.GetComponents.
/// </summary>
internal readonly record struct ShellyVirtualNumber(string? Name, decimal? Value);

/// <summary>
/// Applies the virtual number components published by the phase netted energy script to the energy meter.
/// Not thread-safe: <see cref="ShellyDevice"/> calls it under its update lock.
/// </summary>
internal sealed class ShellyVirtualEnergyCounters
{
    internal const string ImportedComponentName = "TotalImportedEnergy";
    internal const string ExportedComponentName = "TotalExportedEnergy";

    private bool _isRead;
    private bool _hasComponents;
    private int? _readConfigurationRevision;
    private int? _deviceConfigurationRevision;

    // Gen2 devices always report sys.cfg_rev; without it, reads after the first success only happen after a reset.
    /// <summary>
    /// Gets a value indicating whether the next poll reads the components: until a read succeeded, while both components exist,
    /// or when the device configuration revision differs from the one of the last read.
    /// </summary>
    public bool IsReadRequired => !_isRead || _hasComponents || _readConfigurationRevision != _deviceConfigurationRevision;

    public void ObserveConfigurationRevision(int configurationRevision)
    {
        _deviceConfigurationRevision = configurationRevision;
    }

    /// <summary>
    /// Applies the components of a successful read. When both components exist, the meter takes their values.
    /// Otherwise it uses the per-phase sums, or clears its values when it used the script values before.
    /// </summary>
    public void Apply(IReadOnlyList<ShellyVirtualNumber> numbers, int? configurationRevision, ShellyEnergyMeter energyMeter)
    {
        decimal? importedValue = null;
        decimal? exportedValue = null;
        var hasImported = false;
        var hasExported = false;

        // The script uses the first component of each name and ignores duplicates, so the first match is the one it updates.
        foreach (var number in numbers)
        {
            if (number.Name == ImportedComponentName && !hasImported)
            {
                hasImported = true;
                importedValue = number.Value;
            }
            else if (number.Name == ExportedComponentName && !hasExported)
            {
                hasExported = true;
                exportedValue = number.Value;
            }
        }

        // The script always creates both components, so a single one is treated like none.
        _hasComponents = hasImported && hasExported;
        RecordRead(configurationRevision);

        if (!_hasComponents)
        {
            UseFallback(energyMeter);
            return;
        }

        // After a device reboot both components report 0 until the script publishes both again. The script starts at the
        // device counters, so both being 0 is only real while the device counters are 0 too (or not known yet). A single 0
        // is real (e.g. no netted export yet), unless the counter already showed more, since the counters only grow.
        var isRebootReset = importedValue == 0 && exportedValue == 0 &&
            (energyMeter.TotalImportedPhaseEnergy != 0 || energyMeter.TotalExportedPhaseEnergy != 0);

        // A rejected value keeps the current script value, or stays null when switching from the per-phase sums.
        var isScriptSource = energyMeter.IsTotalEnergyPhaseNetted == true;
        energyMeter.UseScriptValues(
            SelectValue(importedValue, energyMeter.TotalImportedEnergy, isRebootReset, isScriptSource),
            SelectValue(exportedValue, energyMeter.TotalExportedEnergy, isRebootReset, isScriptSource));
    }

    private static decimal? SelectValue(decimal? value, decimal? currentValue, bool isRebootReset, bool isScriptSource)
    {
        var isRejected = value == 0 && (isRebootReset || (isScriptSource && currentValue > 0));
        return isRejected ? (isScriptSource ? currentValue : null) : value;
    }

    /// <summary>
    /// Applies a read that failed because the device does not support Shelly.GetComponents: the meter uses the per-phase sums,
    /// or clears its values when it used the script values before, and reads wait for the next configuration revision change.
    /// </summary>
    public void ApplyUnsupported(ShellyEnergyMeter energyMeter)
    {
        _hasComponents = false;
        RecordRead(null);
        UseFallback(energyMeter);
    }

    public void Reset()
    {
        _isRead = false;
        _hasComponents = false;
        _readConfigurationRevision = null;
        _deviceConfigurationRevision = null;
    }

    private void RecordRead(int? configurationRevision)
    {
        _isRead = true;
        _readConfigurationRevision = configurationRevision ?? _deviceConfigurationRevision;
        _deviceConfigurationRevision ??= configurationRevision;
    }

    private static void UseFallback(ShellyEnergyMeter energyMeter)
    {
        // Switching from the script values to the per-phase sums would make the cumulative counters jump.
        if (energyMeter.HasUsedScriptValues)
            energyMeter.ClearTotalEnergy();
        else
            energyMeter.UsePhaseEnergy();
    }

    /// <summary>
    /// Reads the numeric <c>value</c> of a number component status, or <c>null</c> when it is missing or not a decimal number.
    /// </summary>
    private static decimal? ReadValue(JsonElement status)
    {
        return status.ValueKind == JsonValueKind.Object &&
            status.TryGetProperty("value", out var valueElement) &&
            valueElement.ValueKind == JsonValueKind.Number &&
            valueElement.TryGetDecimal(out var value)
                ? value
                : null;
    }

    /// <summary>
    /// Adds the number components of one Shelly.GetComponents page to <paramref name="numbers"/>, skipping malformed components.
    /// </summary>
    /// <returns>The offset of the next page, the total component count and the configuration revision.</returns>
    internal static (int NextOffset, int Total, int? ConfigurationRevision) ReadVirtualNumbers(JsonElement result, List<ShellyVirtualNumber> numbers)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return (0, 0, null);

        var count = 0;
        if (result.TryGetProperty("components", out var components) && components.ValueKind == JsonValueKind.Array)
        {
            foreach (var component in components.EnumerateArray())
            {
                count++;
                if (component.ValueKind != JsonValueKind.Object ||
                    !component.TryGetProperty("key", out var keyElement) ||
                    keyElement.ValueKind != JsonValueKind.String)
                    continue;

                var (componentType, _) = ShellyDevice.ParseComponentKey(keyElement.GetString()!);
                if (componentType != "number")
                    continue;

                var name = component.TryGetProperty("config", out var config) &&
                    config.ValueKind == JsonValueKind.Object &&
                    config.TryGetProperty("name", out var nameElement) &&
                    nameElement.ValueKind == JsonValueKind.String
                        ? nameElement.GetString()
                        : null;

                var value = component.TryGetProperty("status", out var status) ? ReadValue(status) : null;

                numbers.Add(new ShellyVirtualNumber(name, value));
            }
        }

        var offset = TryGetInt32(result, "offset") ?? 0;
        var nextOffset = offset + count;
        var total = count > 0 ? TryGetInt32(result, "total") ?? nextOffset : nextOffset;
        return (nextOffset, total, TryGetInt32(result, "cfg_rev"));
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out var value)
                ? value
                : null;
    }
}
