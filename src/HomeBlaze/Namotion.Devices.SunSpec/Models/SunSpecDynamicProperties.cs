using System.Runtime.CompilerServices;
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
    /// <exception cref="InvalidOperationException">
    /// The subject is not attached to a subject graph with a registry, or a property name is already in use.
    /// </exception>
    public static void AddProperties(IInterceptorSubject subject, SunSpecGroupDefinition group, bool isTopLevel)
    {
        var registered = GetRegisteredSubject(subject);
        var points = SunSpecPointMapping.GetPropertyPoints(group, isTopLevel);
        EnsureNamesAreFree(registered, group, points);

        var ownPointNames = group.Points.Select(point => point.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (point, offset, map) in points)
        {
            // A scale factor in an enclosing group is resolved through the group's IModbusScaleFactorProvider instead.
            var scaleFactorProperty = map.ScaleFactorPointName is { } name && ownPointNames.Contains(name) ? name : null;
            var registerAttribute = CreateRegisterAttribute(map, offset, scaleFactorProperty);
            Attribute[] attributes = map.Kind == SunSpecValueKind.ScaleFactor ? [registerAttribute] : [registerAttribute, CreateStateAttribute(map)];
            AddValueProperty(registered, point.Name, map.PropertyType, null, attributes);
        }

        foreach (var child in group.Groups)
        {
            var propertyName = SunSpecNames.ToPascalCase(child.Name);
            if (child.Count.IsSingle)
            {
                AddValueProperty(registered, propertyName, typeof(SunSpecDynamicGroup), null, []);
            }
            else
            {
                AddValueProperty(registered, propertyName, typeof(SunSpecDynamicGroup[]), Array.Empty<SunSpecDynamicGroup>(), []);
            }
        }
    }

    /// <summary>
    /// Creates, keeps or removes the nested group subjects of an attached owner to match <paramref name="instance"/>,
    /// and adds the properties of new group subjects.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The owner is not attached to a subject graph with a registry, or its group properties were not added.
    /// </exception>
    public static void UpdateGroups(IInterceptorSubject owner, SunSpecGroupDefinition group, SunSpecGroupInstance instance)
    {
        var registered = GetRegisteredSubject(owner);
        foreach (var child in group.Groups)
        {
            var propertyName = SunSpecNames.ToPascalCase(child.Name);
            var property = registered.TryGetProperty(propertyName)
                ?? throw new InvalidOperationException($"The group property {propertyName} has not been added to the subject.");

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

    // Checked before adding any property, because a property added under a used name would replace the existing one's metadata.
    private static void EnsureNamesAreFree(RegisteredSubject registered, SunSpecGroupDefinition group, List<SunSpecPropertyPoint> points)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var propertyNames = points
            .Select(point => point.Point.Name)
            .Concat(group.Groups.Select(child => SunSpecNames.ToPascalCase(child.Name)));

        foreach (var name in propertyNames)
        {
            if (!names.Add(name) || registered.TryGetProperty(name) is not null)
            {
                throw new InvalidOperationException($"The property {name} of group {group.Name} is already defined on {registered.Subject.GetType().Name}.");
            }
        }
    }

    private static void AddValueProperty(RegisteredSubject registered, string name, Type type, object? initialValue, Attribute[] attributes)
    {
        // A reference write is atomic, so readers on other threads never see a torn value.
        var holder = new StrongBox<object?>(initialValue);
        registered.AddProperty(name, type, _ => holder.Value, (_, value) => holder.Value = value, attributes);
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
