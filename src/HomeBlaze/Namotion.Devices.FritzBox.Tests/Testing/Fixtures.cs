namespace Namotion.Devices.FritzBox.Tests.Testing;

internal static class Fixtures
{
    internal static string Read(string name) => File.ReadAllText(GetPath(name));

    internal static MemoryStream Open(string name) => new(File.ReadAllBytes(GetPath(name)));

    private static string GetPath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}
