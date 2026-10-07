using Namotion.Devices.SunSpec.Definitions;

namespace Namotion.Devices.SunSpec.Generator;

/// <summary>
/// Generates the SunSpec model classes of Namotion.Devices.SunSpec from the embedded SunSpec model definitions.
/// </summary>
public static class SunSpecCodeGenerator
{
    // Types the library defines by hand; a generated name must not collide with them.
    private static readonly string[] ReservedTypeNames =
    [
        "ISunSpecModel", "SunSpecDevice", "SunSpecUnit", "SunSpecLogicalDevice", "SunSpecUnknownModel",
        "SunSpecDynamicModel", "SunSpecDynamicGroup", "SunSpecModelFactory"
    ];

    /// <summary>
    /// Generates every file, keyed by file name, with "\n" line endings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Generate()
    {
        var overrides = SunSpecGeneratorOverrides.Load();
        var definitions = SunSpecDefinitions.GetBuiltInModelIds()
            .Where(modelId => !overrides.ExcludedModels.Contains(modelId))
            .Select(modelId => SunSpecDefinitions.TryGetBuiltIn(modelId)!)
            .ToArray();

        var plans = CreatePlans(definitions, overrides);
        var typeNames = new HashSet<string>(ReservedTypeNames, StringComparer.Ordinal);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            files[$"{plan.ClassName}.g.cs"] = SunSpecClassWriter.WriteModelFile(plan, typeNames);
        }

        files["SunSpecModelFactory.g.cs"] = SunSpecClassWriter.WriteFactoryFile(plans);
        return files;
    }

    private static List<SunSpecClassPlan> CreatePlans(IReadOnlyList<SunSpecModelDefinition> definitions, SunSpecGeneratorOverrides overrides)
    {
        var definitionsById = definitions.ToDictionary(definition => definition.Id);
        var assignedModels = new HashSet<int>();
        var plans = new List<SunSpecClassPlan>();

        foreach (var classOverride in overrides.Classes)
        {
            var representative = definitionsById[classOverride.Representative ?? classOverride.Models.Single()];
            foreach (var modelId in classOverride.Models)
            {
                if (!assignedModels.Add(modelId))
                {
                    throw new InvalidDataException($"Model {modelId} is assigned to more than one class.");
                }

                EnsureSameLayout(representative.Group, definitionsById[modelId].Group, modelId);
            }

            plans.Add(new SunSpecClassPlan(classOverride.Name, classOverride.Models, representative));
        }

        foreach (var definition in definitions.Where(definition => !assignedModels.Contains(definition.Id)))
        {
            plans.Add(new SunSpecClassPlan($"SunSpecModel{definition.Id}", [definition.Id], definition));
        }

        return plans.OrderBy(plan => plan.ModelIds[0]).ToList();
    }

    private static void EnsureSameLayout(SunSpecGroupDefinition expected, SunSpecGroupDefinition actual, int modelId)
    {
        var isSame = expected.Points.Count == actual.Points.Count &&
                     expected.Groups.Count == actual.Groups.Count &&
                     expected.Count == actual.Count &&
                     expected.Points.Zip(actual.Points).All(pair => pair.First.Type == pair.Second.Type && pair.First.Size == pair.Second.Size);

        if (!isSame)
        {
            throw new InvalidDataException($"Model {modelId} does not share the layout of its class representative in group {expected.Name}.");
        }

        foreach (var (expectedGroup, actualGroup) in expected.Groups.Zip(actual.Groups))
        {
            EnsureSameLayout(expectedGroup, actualGroup, modelId);
        }
    }
}

/// <summary>
/// One generated model class and the model IDs it represents.
/// </summary>
internal sealed record SunSpecClassPlan(string ClassName, IReadOnlyList<int> ModelIds, SunSpecModelDefinition Definition)
{
    public bool IsFamily => ModelIds.Count > 1;
}
