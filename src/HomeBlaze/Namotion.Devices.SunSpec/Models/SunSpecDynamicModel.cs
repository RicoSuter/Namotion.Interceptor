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
public partial class SunSpecDynamicModel : ISunSpecModel, ITitleProvider, ISunSpecGroupOwner
{
    // A field rather than a property, so it does not show up as a registry property.
    private readonly SunSpecModelDefinition _definition;

    // Discovery state, only touched by the discovery on the source's thread.
    private SunSpecGroupInstance? _layout;
    private bool _hasProperties;

    internal SunSpecDynamicModel(SunSpecModelDefinition definition, int baseAddress, int length)
    {
        _definition = definition;
        BaseAddress = baseAddress;
        Length = length;
        ModelIdRegister = null;
    }

    /// <inheritdoc />
    public int ModelId => _definition.Id;

    /// <inheritdoc />
    public int BaseAddress { get; }

    /// <inheritdoc />
    public int Length { get; }

    /// <inheritdoc />
    public string Title => _definition.Group.Label ?? $"Model {ModelId}";

    /// <inheritdoc />
    [ModbusRegister(0, ModbusDataType.U16, Access = ModbusAccess.ReadOnly)]
    public partial ushort? ModelIdRegister { get; internal set; }

    internal SunSpecModelDefinition GetDefinition() => _definition;

    // Only stored: EnsureProperties applies it once the properties holding the groups exist.
    void ISunSpecGroupOwner.UpdateGroups(SunSpecGroupInstance instance) => _layout = instance;

    /// <summary>
    /// Adds the point and group properties once the model is attached to a subject graph with a registry, then applies
    /// the latest group layout.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The model is not attached to a subject graph with a registry, or a property name is already in use.
    /// </exception>
    internal void EnsureProperties()
    {
        if (!_hasProperties)
        {
            SunSpecDynamicProperties.AddProperties(this, _definition.Group, isTopLevel: true);
            _hasProperties = true;
        }

        if (_layout is { } layout)
        {
            SunSpecDynamicProperties.UpdateGroups(this, _definition.Group, layout);
        }
    }
}
