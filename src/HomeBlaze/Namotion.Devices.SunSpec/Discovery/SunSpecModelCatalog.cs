using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Models;

namespace Namotion.Devices.SunSpec.Discovery;

/// <summary>
/// Chooses the subject for a model: its generated class, a dynamic model from a user definition, or an unknown model.
/// </summary>
internal sealed class SunSpecModelCatalog(IReadOnlyDictionary<int, SunSpecModelDefinition> userDefinitions)
{
    public static SunSpecModelCatalog Empty { get; } = new(new Dictionary<int, SunSpecModelDefinition>());

    /// <summary>
    /// Creates the subject for <paramref name="entry"/>. A generated class wins over a user definition.
    /// </summary>
    public ISunSpecModel Create(SunSpecChainEntry entry)
        => SunSpecModelFactory.TryCreate(entry.ModelId, entry.Address, entry.Length)
           ?? (userDefinitions.TryGetValue(entry.ModelId, out var definition)
               ? new SunSpecDynamicModel(definition, entry.Address, entry.Length)
               : new SunSpecUnknownModel(entry.ModelId, entry.Address, entry.Length));

    /// <summary>
    /// Gets the definition the groups of the model are resolved from, or <c>null</c> for an unknown model.
    /// </summary>
    public SunSpecModelDefinition? TryGetDefinition(int modelId)
        => SunSpecModelFactory.IsGenerated(modelId) ? SunSpecDefinitions.TryGetBuiltIn(modelId) : userDefinitions.GetValueOrDefault(modelId);

    /// <summary>
    /// Gets whether <paramref name="model"/> still is the right subject for <paramref name="entry"/>.
    /// </summary>
    public bool IsCurrent(ISunSpecModel model, SunSpecChainEntry entry)
    {
        if (model.ModelId != entry.ModelId || model.BaseAddress != entry.Address || model.Length != entry.Length)
        {
            return false;
        }

        return model switch
        {
            SunSpecUnknownModel => !SunSpecModelFactory.IsGenerated(entry.ModelId) && !userDefinitions.ContainsKey(entry.ModelId),
            SunSpecDynamicModel dynamicModel => !SunSpecModelFactory.IsGenerated(entry.ModelId)
                && userDefinitions.TryGetValue(entry.ModelId, out var definition)
                && ReferenceEquals(definition, dynamicModel.GetDefinition()),
            _ => SunSpecModelFactory.IsGenerated(entry.ModelId)
        };
    }
}
