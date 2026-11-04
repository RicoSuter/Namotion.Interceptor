using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Common;
using HomeBlaze.Abstractions.Devices;
using Namotion.Devices.SunSpec.Models;
using Namotion.Interceptor.Attributes;

namespace Namotion.Devices.SunSpec;

/// <summary>
/// One physical device in a unit's model chain (an inverter, a meter, a battery): its Common model and the models
/// that follow it until the next Common model.
/// </summary>
[InterceptorSubject]
public partial class SunSpecLogicalDevice : IDeviceInfo, ISoftwareState, ITitleProvider
{
    /// <summary>
    /// Initializes the logical device at position <paramref name="index"/> of the unit <paramref name="unitId"/>.
    /// </summary>
    public SunSpecLogicalDevice(byte unitId, int index)
    {
        UnitId = unitId;
        Index = index;
        Common = null;
        Models = [];
    }

    /// <summary>
    /// Gets the unit ID of the chain this device belongs to.
    /// </summary>
    public byte UnitId { get; }

    /// <summary>
    /// Gets the zero-based position of this device in its unit's chain.
    /// </summary>
    public int Index { get; }

    /// <summary>
    /// Gets the Common model (SunSpec model 1), or <c>null</c> for models before the first Common model of a chain.
    /// </summary>
    public partial SunSpecCommon? Common { get; internal set; }

    /// <summary>
    /// Gets the models after the Common model, in chain order.
    /// </summary>
    public partial ISunSpecModel[] Models { get; internal set; }

    /// <inheritdoc />
    [Derived]
    public string? Manufacturer => Common?.Mn;

    /// <inheritdoc />
    [Derived]
    public string? Model => Common?.Md;

    /// <inheritdoc />
    public string? ProductCode => null;

    /// <inheritdoc />
    [Derived]
    public string? SerialNumber => Common?.SN;

    /// <inheritdoc />
    public string? HardwareRevision => null;

    /// <inheritdoc />
    [Derived]
    public string? SoftwareVersion => Common?.Vr;

    /// <inheritdoc />
    public string? AvailableSoftwareUpdate => null;

    /// <inheritdoc />
    [Derived]
    public string Title
    {
        get
        {
            var manufacturer = Common?.Mn;
            var model = Common?.Md;
            return string.IsNullOrWhiteSpace(manufacturer) && string.IsNullOrWhiteSpace(model)
                ? $"Unit {UnitId}, device {Index + 1}"
                : $"{manufacturer} {model}".Trim();
        }
    }

    /// <summary>
    /// Gets the Common model, if any, followed by the other models.
    /// </summary>
    internal IEnumerable<ISunSpecModel> GetModels() => Common is null ? Models : Models.Prepend(Common);
}
