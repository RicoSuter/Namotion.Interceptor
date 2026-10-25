namespace Namotion.Interceptor.Modbus;

/// <summary>
/// The Modbus data table a mapping reads from.
/// </summary>
public enum ModbusAddressSpace
{
    /// <summary>Holding registers, read with function code 3.</summary>
    HoldingRegister,

    /// <summary>Input registers, read with function code 4.</summary>
    InputRegister,

    /// <summary>Coils, read with function code 1.</summary>
    Coil,

    /// <summary>Discrete inputs, read with function code 2.</summary>
    DiscreteInput
}
