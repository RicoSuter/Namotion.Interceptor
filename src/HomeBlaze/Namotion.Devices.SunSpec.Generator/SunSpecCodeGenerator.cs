using Namotion.Devices.SunSpec.Definitions;

namespace Namotion.Devices.SunSpec.Generator;

/// <summary>
/// Generates the SunSpec model classes of Namotion.Devices.SunSpec from the embedded SunSpec model definitions.
/// </summary>
public static class SunSpecCodeGenerator
{
    // Types written by hand in the library (some by later work), outside the generated files. Together with the
    // Definitions types the generated files import, a generated type must not reuse their names.
    private static readonly string[] HandWrittenTypeNames =
    [
        "ISunSpecModel", "ISunSpecGroupOwner", "SunSpecGroups", "SunSpecDevice", "SunSpecUnit", "SunSpecLogicalDevice",
        "SunSpecUnknownModel", "SunSpecDynamicModel", "SunSpecDynamicGroup", "SunSpecModelFactory"
    ];

    /// <summary>
    /// Generates every file, keyed by file name, with "\n" line endings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Generate()
    {
        var definitions = SunSpecDefinitions.GetBuiltInModelIds()
            .Select(modelId => SunSpecDefinitions.TryGetBuiltIn(modelId)!)
            .ToArray();

        return Generate(definitions, SunSpecGeneratorOverrides.Load());
    }

    /// <summary>
    /// Generates every file for <paramref name="definitions"/>, except the excluded ones, keyed by file name.
    /// </summary>
    /// <exception cref="InvalidDataException">A definition or the overrides cannot be generated.</exception>
    internal static IReadOnlyDictionary<string, string> Generate(IReadOnlyList<SunSpecModelDefinition> definitions, SunSpecGeneratorOverrides overrides)
    {
        var included = definitions.Where(definition => !overrides.ExcludedModels.Contains(definition.Id)).ToArray();
        var plans = CreatePlans(included, overrides);
        var typeNames = GetReservedTypeNames();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var plan in plans)
        {
            files[$"{plan.ClassName}.g.cs"] = SunSpecClassWriter.WriteModelFile(plan, typeNames);
        }

        files["SunSpecModelFactory.g.cs"] = SunSpecClassWriter.WriteFactoryFile(plans);
        return files;
    }

    private static HashSet<string> GetReservedTypeNames()
    {
        var definitionsNamespace = typeof(SunSpecModelDefinition).Namespace;
        var names = new HashSet<string>(HandWrittenTypeNames, StringComparer.Ordinal);
        names.UnionWith(typeof(SunSpecModelDefinition).Assembly.GetTypes()
            .Where(type => type.Namespace == definitionsNamespace && !type.IsNested)
            .Select(type => type.Name));

        return names;
    }

    private static List<SunSpecClassPlan> CreatePlans(IReadOnlyList<SunSpecModelDefinition> definitions, SunSpecGeneratorOverrides overrides)
    {
        var definitionsById = definitions.ToDictionary(definition => definition.Id);
        var assignedModels = new HashSet<int>();
        var plans = new List<SunSpecClassPlan>();

        foreach (var classOverride in overrides.Classes)
        {
            var representativeId = GetRepresentativeId(classOverride);
            var models = classOverride.Models.Select(modelId => GetClassModel(classOverride, modelId, definitionsById, overrides)).ToArray();
            var representative = definitionsById[representativeId];
            foreach (var model in models)
            {
                if (!assignedModels.Add(model.Id))
                {
                    throw new InvalidDataException($"Model {model.Id} is assigned to more than one class.");
                }

                SunSpecFamilyLayout.EnsureSameLayout(representative, model);
            }

            plans.Add(new SunSpecClassPlan(CSharpText.ToIdentifier(classOverride.Name), models, representative));
        }

        foreach (var definition in definitions.Where(definition => !assignedModels.Contains(definition.Id)))
        {
            plans.Add(new SunSpecClassPlan($"SunSpecModel{definition.Id}", [definition], definition));
        }

        return plans.OrderBy(plan => plan.ModelIds[0]).ToList();
    }

    private static int GetRepresentativeId(SunSpecClassOverride classOverride)
    {
        if (classOverride.Models.Count == 0)
        {
            throw new InvalidDataException($"Class {classOverride.Name} in overrides.json lists no models.");
        }

        if (classOverride.Representative is not { } representative)
        {
            return classOverride.Models.Count == 1
                ? classOverride.Models[0]
                : throw new InvalidDataException($"Class {classOverride.Name} in overrides.json lists several models but no representative.");
        }

        return classOverride.Models.Contains(representative)
            ? representative
            : throw new InvalidDataException($"The representative {representative} of class {classOverride.Name} in overrides.json is not one of its models.");
    }

    private static SunSpecModelDefinition GetClassModel(
        SunSpecClassOverride classOverride, int modelId, Dictionary<int, SunSpecModelDefinition> definitionsById, SunSpecGeneratorOverrides overrides)
    {
        if (overrides.ExcludedModels.Contains(modelId))
        {
            throw new InvalidDataException($"Class {classOverride.Name} in overrides.json lists model {modelId}, which is excluded.");
        }

        return definitionsById.TryGetValue(modelId, out var definition)
            ? definition
            : throw new InvalidDataException($"Class {classOverride.Name} in overrides.json lists model {modelId}, which has no definition.");
    }
}

/// <summary>
/// One generated model class: the models it represents and the representative whose layout it is generated from.
/// </summary>
internal sealed record SunSpecClassPlan(string ClassName, IReadOnlyList<SunSpecModelDefinition> Models, SunSpecModelDefinition Definition)
{
    public IReadOnlyList<int> ModelIds { get; } = Models.Select(model => model.Id).ToArray();

    public bool IsFamily => Models.Count > 1;
}
