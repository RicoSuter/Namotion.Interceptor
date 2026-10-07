using HomeBlaze.Abstractions.Attributes;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Interceptor;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace Namotion.Devices.SunSpec.Models;

/// <summary>
/// Adds the properties of dynamic models and groups from a SunSpec definition.
/// </summary>
internal static class SunSpecDynamicProperties
{
    /// <summary>
    /// Adds a property per mapped point of <paramref name="group"/>, addressed relative to the subject's base address,
    /// and a property per nested group. The ID and length points of a top-level group are skipped.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject is not attached to a subject graph with a registry.</exception>
    public static void AddProperties(ISunSpecDynamicSubject subject, SunSpecGroupDefinition group, bool isTopLevel)
    {
        var registered = GetRegisteredSubject((IInterceptorSubject)subject);

        var ownPointNames = group.Points.Select(point => point.Name).ToHashSet(StringComparer.Ordinal);
        var offset = 0;
        for (var index = 0; index < group.Points.Count; index++)
        {
            var point = group.Points[index];
            var pointOffset = offset;
            offset += point.Size;
            if ((isTopLevel && index < 2) || SunSpecPointMapping.TryMap(point) is not { } map)
            {
                continue;
            }

            // A scale factor in an enclosing group is resolved through the group's IModbusScaleFactorProvider instead.
            var scaleFactorProperty = map.ScaleFactorPointName is { } name && ownPointNames.Contains(name) ? name : null;
            var registerAttribute = CreateRegisterAttribute(map, pointOffset, scaleFactorProperty);
            Attribute[] attributes = map.Kind == SunSpecValueKind.ScaleFactor ? [registerAttribute] : [registerAttribute, CreateStateAttribute(map)];
            AddValueProperty(registered, point.Name, map.PropertyType, attributes);
        }

        foreach (var child in group.Groups)
        {
            var type = child.Count.IsSingle ? typeof(SunSpecDynamicGroup) : typeof(SunSpecDynamicGroup[]);
            AddValueProperty(registered, SunSpecNames.ToPascalCase(child.Name), type, []);
        }
    }

    /// <summary>
    /// Creates, keeps or removes the nested group subjects of an attached owner to match <paramref name="instance"/>,
    /// and adds the properties of new group subjects.
    /// </summary>
    /// <exception cref="InvalidOperationException">The owner is not attached to a subject graph with a registry.</exception>
    public static void UpdateGroups(IInterceptorSubject owner, SunSpecGroupDefinition group, SunSpecGroupInstance instance)
    {
        var registered = GetRegisteredSubject(owner);
        foreach (var child in group.Groups)
        {
            var property = registered.TryGetProperty(SunSpecNames.ToPascalCase(child.Name))!;
            var instances = instance.GetGroup(child.Name);
            SunSpecDynamicGroup[] groups;
            if (child.Count.IsSingle)
            {
                var current = property.GetValue() as SunSpecDynamicGroup;
                var next = SunSpecGroups.UpdateSingle(current, instances, groupInstance => new SunSpecDynamicGroup(owner, child, groupInstance.Address, groupInstance.Index));
                if (!ReferenceEquals(current, next))
                {
                    property.SetValue(next);
                }

                groups = next is null ? [] : [next];
            }
            else
            {
                var current = property.GetValue() as SunSpecDynamicGroup[] ?? [];
                groups = SunSpecGroups.Update(current, instances, groupInstance => new SunSpecDynamicGroup(owner, child, groupInstance.Address, groupInstance.Index));
                if (!ReferenceEquals(current, groups))
                {
                    property.SetValue(groups);
                }
            }

            // New groups are attached by the assignment above, so their properties can only be added afterwards.
            foreach (var dynamicGroup in groups)
            {
                dynamicGroup.EnsureProperties();
            }
        }
    }

    private static RegisteredSubject GetRegisteredSubject(IInterceptorSubject subject)
        => subject.TryGetRegisteredSubject()
            ?? throw new InvalidOperationException("The subject is not attached to a subject graph with a registry.");

    private static void AddValueProperty(RegisteredSubject registered, string name, Type type, Attribute[] attributes)
    {
        registered.AddProperty(
            name,
            type,
            subject => ((ISunSpecDynamicSubject)subject).Values.GetValueOrDefault(name),
            (subject, value) => ((ISunSpecDynamicSubject)subject).Values[name] = value,
            attributes);
    }

    private static ModbusRegisterAttribute CreateRegisterAttribute(SunSpecPointMap map, int offset, string? scaleFactorProperty) => new(offset, map.DataType)
    {
        Length = map.StringLength,
        Scale = (double)map.Scale,
        ScaleFactorProperty = scaleFactorProperty,
        NotAvailableValue = map.NotAvailableValue,
        Access = map.IsReadOnly ? ModbusAccess.ReadOnly : ModbusAccess.ReadWrite
    };

    private static StateAttribute CreateStateAttribute(SunSpecPointMap map)
    {
        var attribute = new StateAttribute(map.Title);
        if (map.Unit != StateUnit.Default)
        {
            attribute.Unit = map.Unit;
        }

        if (map.IsCumulative)
        {
            attribute.IsCumulative = true;
        }

        if (map.Kind is SunSpecValueKind.Enumeration or SunSpecValueKind.Flags)
        {
            attribute.IsDiscrete = true;
        }

        return attribute;
    }
}
