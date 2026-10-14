using System.Text.Json;

namespace Namotion.Devices.FritzBox.Parsing;

internal static class MeshListParser
{
    /// <summary>
    /// Reads the mesh list. Throws <see cref="JsonException"/> when it is malformed, empty or nests deeper than the
    /// serializer's default limit of 64.
    /// </summary>
    internal static MeshList Parse(Stream stream) =>
        JsonSerializer.Deserialize(stream, MeshJsonContext.Default.MeshList)
        ?? throw new JsonException("The mesh list is empty.");
}
