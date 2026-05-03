namespace Namotion.NuGet.Plugins.Tests.Loading;

internal static class IntegrationTestHelper
{
    public static string FindPluginsFolder()
    {
        var directory = Path.GetDirectoryName(typeof(IntegrationTestHelper).Assembly.Location)!;
        while (directory != null)
        {
            var pluginsPath = Path.Combine(directory, "HomeBlaze", "Plugins");
            if (Directory.Exists(pluginsPath) &&
                File.Exists(Path.Combine(pluginsPath, "MyCompany.SamplePlugin1.HomeBlaze.1.0.0.nupkg")))
            {
                return pluginsPath;
            }

            pluginsPath = Path.Combine(directory, "Plugins");
            if (Directory.Exists(pluginsPath) &&
                Path.GetFileName(directory) == "HomeBlaze" &&
                File.Exists(Path.Combine(pluginsPath, "MyCompany.SamplePlugin1.HomeBlaze.1.0.0.nupkg")))
            {
                return pluginsPath;
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("Could not find Plugins folder. Build the solution first.");
    }
}
