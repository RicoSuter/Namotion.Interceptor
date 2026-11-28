namespace Namotion.Matter;

/// <summary>A value sent to a node, such as a command payload or an attribute value.</summary>
public interface IMatterPayload
{
    /// <summary>Writes the value.</summary>
    /// <param name="writer">The writer.</param>
    void Write(IMatterValueWriter writer);
}
