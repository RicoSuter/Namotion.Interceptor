namespace Namotion.Devices.SunSpec.Definitions;

/// <summary>
/// The position of one group instance within a model.
/// </summary>
internal sealed class SunSpecGroupInstance
{
    public required SunSpecGroupDefinition Definition { get; init; }

    /// <summary>Gets the absolute address of the instance's first register.</summary>
    public required int Address { get; init; }

    /// <summary>Gets the zero-based position within its group.</summary>
    public required int Index { get; init; }

    /// <summary>Gets the register count, including nested groups.</summary>
    public required int Length { get; init; }

    public required IReadOnlyDictionary<string, IReadOnlyList<SunSpecGroupInstance>> Groups { get; init; }

    /// <summary>Gets the instances of the nested group <paramref name="name"/>, empty when there are none.</summary>
    public IReadOnlyList<SunSpecGroupInstance> GetGroup(string name)
        => Groups.TryGetValue(name, out var instances) ? instances : [];
}

/// <summary>
/// Resolves where the groups of a model are, from its definition and its registers.
/// </summary>
internal static class SunSpecLayout
{
    private const ushort NotImplementedCount = 0xFFFF;

    /// <summary>
    /// Resolves the group instances of a model whose ID register is at <paramref name="modelAddress"/>;
    /// <paramref name="registers"/> holds the whole model, starting with the ID and length registers.
    /// A count point reading 0xFFFF (not implemented) yields no instances.
    /// </summary>
    /// <exception cref="InvalidDataException">The groups do not fit the model.</exception>
    public static SunSpecGroupInstance Resolve(SunSpecModelDefinition definition, int modelAddress, ushort[] registers)
    {
        var context = new LayoutContext(definition.Id, modelAddress, registers);
        return ResolveInstance(context, definition.Group, modelAddress, 0, parent: null);
    }

    private static SunSpecGroupInstance ResolveInstance(LayoutContext context, SunSpecGroupDefinition group, int address, int index, LayoutScope? parent)
    {
        var offset = address + GetPointsSize(group);
        var scope = new LayoutScope(group, address, parent);
        var groups = new Dictionary<string, IReadOnlyList<SunSpecGroupInstance>>(group.Groups.Count);

        foreach (var child in group.Groups)
        {
            var instances = new List<SunSpecGroupInstance>();
            offset = child.Count.IsFill
                ? ResolveFill(context, child, offset, scope, instances)
                : ResolveCounted(context, child, offset, scope, instances);

            groups[child.Name] = instances;
        }

        return new SunSpecGroupInstance { Definition = group, Address = address, Index = index, Length = offset - address, Groups = groups };
    }

    // Adds instances while they fit; a partial trailing instance is not part of the group.
    private static int ResolveFill(LayoutContext context, SunSpecGroupDefinition group, int offset, LayoutScope scope, List<SunSpecGroupInstance> instances)
    {
        var pointsSize = GetPointsSize(group);
        while (pointsSize > 0 && offset + pointsSize <= context.End)
        {
            var instance = ResolveInstance(context, group, offset, instances.Count, scope);
            if (offset + instance.Length > context.End)
            {
                break;
            }

            instances.Add(instance);
            offset += instance.Length;
        }

        return offset;
    }

    private static int ResolveCounted(LayoutContext context, SunSpecGroupDefinition group, int offset, LayoutScope scope, List<SunSpecGroupInstance> instances)
    {
        var isCountedByPoint = group.Count.PointName is not null && group.Count.Fixed is null;
        var count = group.Count.IsSingle ? 1 : group.Count.Fixed ?? ReadCount(context, group.Count.PointName!, scope);
        for (var instanceIndex = 0; instanceIndex < count; instanceIndex++)
        {
            var instance = ResolveInstance(context, group, offset, instanceIndex, scope);
            offset += instance.Length;

            // Checked per instance so a device-reported count cannot create more instances than fit the model.
            if (offset > context.End || (isCountedByPoint && instance.Length == 0))
            {
                throw new InvalidDataException($"Group {group.Name} of model {context.ModelId} runs past the model length.");
            }

            instances.Add(instance);
        }

        return offset;
    }

    // A count point lives in an enclosing group instance, the nearest one first.
    private static int ReadCount(LayoutContext context, string pointName, LayoutScope scope)
    {
        for (var level = scope; level is not null; level = level.Parent)
        {
            var pointOffset = GetPointOffset(level.Group, pointName);
            if (pointOffset < 0)
            {
                continue;
            }

            var registerIndex = level.Address + pointOffset - context.ModelAddress;
            if (registerIndex >= context.Registers.Length)
            {
                throw new InvalidDataException($"Count point {pointName} of model {context.ModelId} lies past the model length.");
            }

            var count = context.Registers[registerIndex];
            return count == NotImplementedCount ? 0 : count;
        }

        throw new InvalidDataException($"Count point {pointName} of model {context.ModelId} is not defined.");
    }

    // Returns the register offset of the point within its group, or -1 when the group has no such point.
    private static int GetPointOffset(SunSpecGroupDefinition group, string pointName)
    {
        var offset = 0;
        foreach (var point in group.Points)
        {
            if (point.Name == pointName)
            {
                return offset;
            }

            offset += point.Size;
        }

        return -1;
    }

    private static int GetPointsSize(SunSpecGroupDefinition group)
    {
        var size = 0;
        foreach (var point in group.Points)
        {
            size += point.Size;
        }

        return size;
    }

    private sealed record LayoutScope(SunSpecGroupDefinition Group, int Address, LayoutScope? Parent);

    private sealed record LayoutContext(int ModelId, int ModelAddress, ushort[] Registers)
    {
        public int End => ModelAddress + Registers.Length;
    }
}
