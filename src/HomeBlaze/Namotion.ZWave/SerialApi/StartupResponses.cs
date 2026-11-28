using System.Buffers.Binary;
using System.Text;

namespace Namotion.ZWave.SerialApi;

internal static class StartupResponses
{
    private const int FunctionBitmaskLength = 32;

    public static (string LibraryVersion, ZWaveLibraryType LibraryType) ParseControllerVersion(byte[] payload)
    {
        var terminator = Array.IndexOf(payload, (byte)0);
        if (terminator < 0 || terminator + 1 >= payload.Length)
        {
            throw Invalid("GetControllerVersion");
        }

        return (Encoding.ASCII.GetString(payload, 0, terminator), (ZWaveLibraryType)payload[terminator + 1]);
    }

    public static (uint HomeId, byte OwnNodeId) ParseControllerId(byte[] payload)
    {
        if (payload.Length < 5)
        {
            throw Invalid("GetControllerId");
        }

        return (BinaryPrimitives.ReadUInt32BigEndian(payload), payload[4]);
    }

    public static SerialApiCapabilities ParseSerialApiCapabilities(byte[] payload)
    {
        if (payload.Length < 8 + FunctionBitmaskLength)
        {
            throw Invalid("GetSerialApiCapabilities");
        }

        return new SerialApiCapabilities(
            $"{payload[0]}.{payload[1]}",
            BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2)),
            BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(4)),
            BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(6)),
            payload.AsSpan(8, FunctionBitmaskLength).ToArray());
    }

    public static byte[] ParseNodeIds(byte[] payload)
    {
        if (payload.Length < 3 || payload.Length < 3 + payload[2])
        {
            throw Invalid("GetSerialApiInitData");
        }

        var bitmask = payload.AsSpan(3, payload[2]);
        var nodeIds = new List<byte>();
        for (var index = 0; index < bitmask.Length; index++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                if ((bitmask[index] & (1 << bit)) != 0)
                {
                    nodeIds.Add((byte)(index * 8 + bit + 1));
                }
            }
        }

        return nodeIds.ToArray();
    }

    public static ZWaveControllerCapabilities ParseControllerCapabilities(byte[] payload)
    {
        if (payload.Length < 1)
        {
            throw Invalid("GetControllerCapabilities");
        }

        return (ZWaveControllerCapabilities)payload[0];
    }

    private static ZWaveException Invalid(string function) =>
        new($"The Z-Wave controller sent an invalid response to {function}.");
}

internal sealed record SerialApiCapabilities(string Version, ushort ManufacturerId, ushort ProductType, ushort ProductId, byte[] FunctionBitmask)
{
    public bool SupportsFunction(byte function) =>
        function > 0 && (FunctionBitmask[(function - 1) / 8] & (1 << ((function - 1) % 8))) != 0;
}
