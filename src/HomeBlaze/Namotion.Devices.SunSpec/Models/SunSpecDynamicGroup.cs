using System.Collections.Concurrent;
using HomeBlaze.Abstractions;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec.Models;

/// <summary>
/// A group instance of a <see cref="SunSpecDynamicModel"/>. Its points become dynamic properties once it is attached.
/// </summary>
[InterceptorSubject]
public partial class SunSpecDynamicGroup : IModbusBaseAddressProvider, IModbusScaleFactorProvider, ITitleProvider, ISunSpecGroupOwner, ISunSpecDynamicSubject
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);

    // Discovery state, only touched by the discovery on the source's thread.
    private SunSpecGroupInstance? _layout;
    private bool _hasProperties;

    internal SunSpecDynamicGroup(IInterceptorSubject parent, SunSpecGroupDefinition definition, int baseAddress, int index)
    {
        Parent = parent;
        Definition = definition;
        BaseAddress = baseAddress;
        Index = index;
    }

    /// <summary>
    /// Gets the model or group containing this group.
    /// </summary>
    public IInterceptorSubject Parent { get; }

    internal SunSpecGroupDefinition Definition { get; }

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <summary>
    /// Gets the zero-based position of this instance within its group.
    /// </summary>
    public int Index { get; }

    /// <inheritdoc />
    public string Title => $"{Definition.Label ?? SunSpecNames.ToPascalCase(Definition.Name)} {Index + 1}";

    ConcurrentDictionary<string, object?> ISunSpecDynamicSubject.Values => _values;

    /// <inheritdoc />
    public PropertyReference? TryGetScaleFactorProperty(string propertyName)
    {
        var point = Definition.Points.FirstOrDefault(candidate => candidate.Name == propertyName);
        if (point?.ScaleFactor.PointName is not { } scaleFactorName || ContainsPoint(Definition, scaleFactorName))
        {
            return null;
        }

        // The scale factor lives in the nearest enclosing group defining it.
        for (var ancestor = Parent; ancestor is not null; ancestor = (ancestor as SunSpecDynamicGroup)?.Parent)
        {
            if (GetDefinition(ancestor) is { } group && ContainsPoint(group, scaleFactorName))
            {
                return new PropertyReference(ancestor, scaleFactorName);
            }
        }

        return null;
    }

    private static SunSpecGroupDefinition? GetDefinition(IInterceptorSubject subject) => subject switch
    {
        SunSpecDynamicGroup dynamicGroup => dynamicGroup.Definition,
        SunSpecDynamicModel dynamicModel => dynamicModel.Definition.Group,
        _ => null
    };

    void ISunSpecGroupOwner.UpdateGroups(SunSpecGroupInstance instance)
    {
        _layout = instance;
        if (_hasProperties)
        {
            SunSpecDynamicProperties.UpdateGroups(this, Definition, instance);
        }
    }

    /// <summary>
    /// Adds the point and group properties once the group is attached, then applies the latest layout of its nested groups.
    /// </summary>
    /// <exception cref="InvalidOperationException">The group is not attached to a subject graph with a registry.</exception>
    internal void EnsureProperties()
    {
        if (!_hasProperties)
        {
            SunSpecDynamicProperties.AddProperties(this, Definition, isTopLevel: false);
            _hasProperties = true;
        }

        if (_layout is { } layout)
        {
            SunSpecDynamicProperties.UpdateGroups(this, Definition, layout);
        }
    }

    private static bool ContainsPoint(SunSpecGroupDefinition group, string pointName)
        => group.Points.Any(candidate => candidate.Name == pointName);
}
