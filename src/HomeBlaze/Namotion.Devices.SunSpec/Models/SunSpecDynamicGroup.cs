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
public partial class SunSpecDynamicGroup : IModbusBaseAddressProvider, IModbusScaleFactorProvider, ITitleProvider, ISunSpecGroupOwner
{
    // A field rather than a property, so it does not show up as a registry property.
    private readonly SunSpecGroupDefinition _definition;

    // Discovery state, only touched by the discovery on the source's thread.
    private SunSpecGroupInstance? _layout;
    private bool _hasProperties;

    internal SunSpecDynamicGroup(IInterceptorSubject parent, SunSpecGroupDefinition definition, int baseAddress, int index)
    {
        Parent = parent;
        _definition = definition;
        BaseAddress = baseAddress;
        Index = index;
    }

    /// <summary>
    /// Gets the model or group containing this group.
    /// </summary>
    public IInterceptorSubject Parent { get; }

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <summary>
    /// Gets the zero-based position of this instance within its group.
    /// </summary>
    public int Index { get; }

    /// <inheritdoc />
    public string Title => $"{_definition.Label ?? SunSpecNames.ToPascalCase(_definition.Name)} {Index + 1}";

    internal SunSpecGroupDefinition GetDefinition() => _definition;

    /// <inheritdoc />
    public PropertyReference? TryGetScaleFactorProperty(string propertyName)
    {
        if (GetEnclosingScaleFactorName(propertyName) is not { } scaleFactorName)
        {
            return null;
        }

        // The scale factor lives in the nearest enclosing group defining it.
        for (var ancestor = Parent; ancestor is not null; ancestor = (ancestor as SunSpecDynamicGroup)?.Parent)
        {
            if (GetGroupDefinition(ancestor) is { } group && ContainsPoint(group, scaleFactorName))
            {
                return new PropertyReference(ancestor, scaleFactorName);
            }
        }

        return null;
    }

    // Only stored: EnsureProperties applies it once the properties holding the nested groups exist.
    void ISunSpecGroupOwner.UpdateGroups(SunSpecGroupInstance instance) => _layout = instance;

    /// <summary>
    /// Adds the point and group properties once the group is attached, then applies the latest layout of its nested groups.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The group is not attached to a subject graph with a registry, or a property name is already in use.
    /// </exception>
    internal void EnsureProperties()
    {
        if (!_hasProperties)
        {
            SunSpecDynamicProperties.AddProperties(this, _definition, isTopLevel: false);
            _hasProperties = true;
        }

        if (_layout is { } layout)
        {
            SunSpecDynamicProperties.UpdateGroups(this, _definition, layout);
        }
    }

    // Returns the scale factor point of the point property, or null when there is none or it is in this group, where
    // the register attribute already names it.
    private string? GetEnclosingScaleFactorName(string propertyName)
    {
        var point = _definition.Points.FirstOrDefault(candidate => candidate.Name == propertyName);
        var scaleFactorName = point is null ? null : SunSpecPointMapping.TryMap(point)?.ScaleFactorPointName;
        return scaleFactorName is null || ContainsPoint(_definition, scaleFactorName) ? null : scaleFactorName;
    }

    private static SunSpecGroupDefinition? GetGroupDefinition(IInterceptorSubject subject) => subject switch
    {
        SunSpecDynamicGroup dynamicGroup => dynamicGroup.GetDefinition(),
        SunSpecDynamicModel dynamicModel => dynamicModel.GetDefinition().Group,
        _ => null
    };

    private static bool ContainsPoint(SunSpecGroupDefinition group, string pointName)
        => group.Points.Any(candidate => candidate.Name == pointName);
}
