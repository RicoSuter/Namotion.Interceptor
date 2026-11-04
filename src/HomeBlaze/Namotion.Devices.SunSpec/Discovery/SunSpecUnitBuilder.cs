using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Models;

namespace Namotion.Devices.SunSpec.Discovery;

/// <summary>
/// Turns a chain into logical devices, keeping the subjects of models that did not change.
/// </summary>
internal static class SunSpecUnitBuilder
{
    /// <summary>
    /// Builds the logical devices of the unit <paramref name="unitId"/>, reusing the devices of <paramref name="current"/>
    /// by position and its models by address, model ID and length. Returns <paramref name="current"/> itself when the
    /// number of devices is unchanged.
    /// </summary>
    /// <exception cref="InvalidDataException">A model's groups do not fit its length.</exception>
    public static SunSpecLogicalDevice[] Build(byte unitId, SunSpecChain chain, SunSpecLogicalDevice[] current, SunSpecModelCatalog catalog)
    {
        var existingModels = GetExistingModels(current);

        var groups = new List<(SunSpecCommon? Common, List<ISunSpecModel> Models)>();
        foreach (var entry in chain.Models)
        {
            var model = GetModel(entry, existingModels, catalog);
            if (model is SunSpecCommon common)
            {
                groups.Add((common, []));
            }
            else
            {
                if (groups.Count == 0)
                {
                    groups.Add((null, []));
                }

                groups[^1].Models.Add(model);
            }
        }

        var devices = current.Length == groups.Count ? current : new SunSpecLogicalDevice[groups.Count];
        for (var index = 0; index < groups.Count; index++)
        {
            var device = index < current.Length ? current[index] : new SunSpecLogicalDevice(unitId, index);
            UpdateDevice(device, groups[index].Common, groups[index].Models);

            // The current array is published, and collections are replaced, never mutated.
            if (!ReferenceEquals(devices, current))
            {
                devices[index] = device;
            }
        }

        return devices;
    }

    private static Dictionary<int, ISunSpecModel> GetExistingModels(SunSpecLogicalDevice[] devices)
    {
        var models = new Dictionary<int, ISunSpecModel>();
        foreach (var device in devices)
        {
            foreach (var model in device.GetModels())
            {
                models[model.BaseAddress] = model;
            }
        }

        return models;
    }

    private static ISunSpecModel GetModel(SunSpecChainEntry entry, Dictionary<int, ISunSpecModel> existingModels, SunSpecModelCatalog catalog)
    {
        var model = existingModels.TryGetValue(entry.Address, out var existing) && catalog.IsCurrent(existing, entry)
            ? existing
            : catalog.Create(entry);

        if (model is ISunSpecGroupOwner owner && catalog.TryGetDefinition(entry.ModelId) is { } definition)
        {
            owner.UpdateGroups(SunSpecLayout.Resolve(definition, entry.Address, entry.Registers));
        }

        return model;
    }

    private static void UpdateDevice(SunSpecLogicalDevice device, SunSpecCommon? common, List<ISunSpecModel> models)
    {
        if (!ReferenceEquals(device.Common, common))
        {
            device.Common = common;
        }

        if (!device.Models.SequenceEqual(models))
        {
            device.Models = [.. models];
        }
    }
}
