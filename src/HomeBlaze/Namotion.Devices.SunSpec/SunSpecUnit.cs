using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec;

/// <summary>
/// A Modbus unit ID of a SunSpec device and the logical devices in its model chain.
/// </summary>
[InterceptorSubject]
public partial class SunSpecUnit : IModbusUnitIdProvider, ITitleProvider
{
    /// <summary>
    /// Initializes the unit <paramref name="unitId"/>.
    /// </summary>
    public SunSpecUnit(byte unitId)
    {
        UnitId = unitId;
        MarkerAddress = null;
        Devices = [];
    }

    /// <inheritdoc />
    public byte UnitId { get; }

    /// <summary>
    /// Gets the address of the "SunS" marker the chain starts at (40000, 50000 or 0), or <c>null</c> before discovery.
    /// </summary>
    [State]
    public partial int? MarkerAddress { get; internal set; }

    /// <summary>
    /// Gets the logical devices in chain order; each starts with its Common model, except the first one when the chain
    /// does not start with a Common model.
    /// </summary>
    public partial SunSpecLogicalDevice[] Devices { get; internal set; }

    /// <inheritdoc />
    public string Title => $"Unit {UnitId}";

    // The chain as logged last, so discovery logs it again only when it changes.
    internal string? ChainDescription { get; set; }
}
