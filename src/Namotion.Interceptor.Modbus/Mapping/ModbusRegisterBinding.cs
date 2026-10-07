using Namotion.Interceptor.Modbus.Attributes;

namespace Namotion.Interceptor.Modbus.Mapping;

/// <summary>
/// One resolved <see cref="ModbusRegisterAttribute"/> property plus its poll-cycle state.
/// </summary>
internal sealed class ModbusRegisterBinding
{
    private int _reapplyRequested;

    public ModbusRegisterBinding(
        PropertyReference property, string path, byte unitId, int address,
        ModbusRegisterAttribute attribute, ModbusValueReader reader)
    {
        Property = property;
        Path = path;
        UnitId = unitId;
        Address = address;
        Attribute = attribute;
        Reader = reader;
        Count = ModbusRegisterCodec.GetRegisterCount(attribute.DataType, attribute.Length);
        IsBitSpace = attribute.AddressSpace.IsBitSpace();

        var rawLength = IsBitSpace ? 1 : Count * 2;
        CurrentRaw = new byte[rawLength];
        LastRaw = new byte[rawLength];
    }

    public PropertyReference Property { get; }

    public string Path { get; }

    public byte UnitId { get; }

    public int Address { get; }

    /// <summary>
    /// Gets the number of registers, or 1 for a bit.
    /// </summary>
    public int Count { get; }

    public bool IsBitSpace { get; }

    public ModbusAddressSpace AddressSpace => Attribute.AddressSpace;

    public ModbusRegisterAttribute Attribute { get; }

    public ModbusValueReader Reader { get; }

    public ModbusRegisterBinding? ScaleFactor { get; set; }

    /// <summary>
    /// Gets the property holding the scale factor of this mapping, from <see cref="ModbusRegisterAttribute.ScaleFactorProperty"/>
    /// or an <see cref="IModbusScaleFactorProvider"/>, or <c>null</c> when the mapping has no dynamic scale factor.
    /// </summary>
    public PropertyReference? ScaleFactorReference { get; init; }

    // Poll-cycle state: only the read and the apply of one cycle touch it, and the source runs one cycle at a time.
    public byte[] CurrentRaw { get; }

    public byte[] LastRaw { get; }

    public bool HasCurrent { get; set; }

    public bool HasLast { get; set; }

    public bool ChangedThisCycle { get; set; }

    public bool IsUnavailable { get; set; }

    /// <summary>
    /// Gets or sets the confirming read of a binding larger than one request that disagreed with its first read, allocated on
    /// the first disagreement. Holds a value only while <see cref="HasCandidate"/> is set.
    /// </summary>
    public byte[]? CandidateRaw { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="CandidateRaw"/> holds a complete read that becomes current when the next first
    /// read agrees with it.
    /// </summary>
    public bool HasCandidate { get; set; }

    /// <summary>
    /// Gets or sets the number of cycles in a row in which the two reads of a binding larger than one request disagreed.
    /// </summary>
    public int ConsecutiveMismatchCount { get; set; }

    /// <summary>
    /// Gets or sets whether this binding is read in a request of its own, set after a request spanning it was
    /// rejected. Holds until the next connect creates new bindings.
    /// </summary>
    public bool IsIsolated { get; set; }

    // Requested from the change queue thread, consumed by the poll loop.
    public void RequestReapply() => Volatile.Write(ref _reapplyRequested, 1);

    public bool ConsumeReapplyRequest() => Interlocked.Exchange(ref _reapplyRequested, 0) == 1;
}
