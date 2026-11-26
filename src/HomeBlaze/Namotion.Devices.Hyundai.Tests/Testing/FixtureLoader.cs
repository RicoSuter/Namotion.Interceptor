namespace Namotion.Devices.Hyundai.Tests.Testing;

internal static class FixtureLoader
{
    public static string Load(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
