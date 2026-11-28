namespace Namotion.Matter;

/// <summary>Creates payloads.</summary>
public static class MatterPayload
{
    /// <summary>Gets a payload that is a struct without fields, the payload of a command without parameters.</summary>
    public static IMatterPayload Empty { get; } = new DelegatePayload(static writer =>
    {
        writer.WriteStartStruct();
        writer.WriteEndStruct();
    });

    /// <summary>Creates a payload that a delegate writes.</summary>
    /// <param name="write">Writes the value.</param>
    public static IMatterPayload Create(Action<IMatterValueWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return new DelegatePayload(write);
    }

    private sealed class DelegatePayload(Action<IMatterValueWriter> write) : IMatterPayload
    {
        public void Write(IMatterValueWriter writer) => write(writer);
    }
}
