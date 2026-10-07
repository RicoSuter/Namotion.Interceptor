using System.Collections.Concurrent;
using HomeBlaze.Abstractions;
using Namotion.Devices.SunSpec.Definitions;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;
using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Devices.SunSpec.Models;

/// <summary>
/// A model read from a user-supplied SunSpec definition. Its points become dynamic properties once it is attached.
/// </summary>
[InterceptorSubject]
public partial class SunSpecDynamicModel : ISunSpecModel, ITitleProvider, ISunSpecGroupOwner, ISunSpecDynamicSubject
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);

    // Discovery state, only touched by the discovery on the source's thread.
    private SunSpecGroupInstance? _layout;
    private bool _hasProperties;

    internal SunSpecDynamicModel(SunSpecModelDefinition definition, int baseAddress, int length)
    {
        Definition = definition;
        BaseAddress = baseAddress;
        Length = length;
        ModelIdRegister = null;
    }

    internal SunSpecModelDefinition Definition { get; }

    /// <inheritdoc />
    public int ModelId => Definition.Id;

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <inheritdoc />
    public int Length { get; }

    /// <inheritdoc />
    public string Title => Definition.Group.Label ?? $"Model {ModelId}";

    /// <inheritdoc />
    [ModbusRegister(0, ModbusDataType.U16, Access = ModbusAccess.ReadOnly)]
    public partial ushort? ModelIdRegister { get; internal set; }

    ConcurrentDictionary<string, object?> ISunSpecDynamicSubject.Values => _values;

    void ISunSpecGroupOwner.UpdateGroups(SunSpecGroupInstance instance)
    {
        _layout = instance;
        if (_hasProperties)
        {
            SunSpecDynamicProperties.UpdateGroups(this, Definition.Group, instance);
        }
    }

    /// <summary>
    /// Adds the point and group properties once the model is attached to a subject graph with a registry, then applies
    /// the latest group layout.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model is not attached to a subject graph with a registry.</exception>
    internal void EnsureProperties()
    {
        if (!_hasProperties)
        {
            SunSpecDynamicProperties.AddProperties(this, Definition.Group, isTopLevel: true);
            _hasProperties = true;
        }

        if (_layout is { } layout)
        {
            SunSpecDynamicProperties.UpdateGroups(this, Definition.Group, layout);
        }
    }
}
