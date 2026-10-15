using System.Reflection;
using System.Runtime.Loader;

namespace HomeBlaze.Services.Diagnostics;

/// <summary>
/// Reads the assemblies of all load contexts, without dynamic and shared framework assemblies.
/// </summary>
internal static class LoadedAssemblyReader
{
    private static readonly string? SharedFrameworkRoot = GetSharedFrameworkRoot();

    public static List<LoadedAssembly> Read()
    {
        var assemblies = new List<LoadedAssembly>();
        foreach (var context in AssemblyLoadContext.All)
        {
            var contextName = context == AssemblyLoadContext.Default
                ? AssemblyInventory.DefaultLoadContextName
                : context.Name ?? "Unnamed";

            foreach (var assembly in context.Assemblies)
            {
                if (!assembly.IsDynamic && !IsSharedFrameworkAssembly(assembly))
                {
                    assemblies.Add(Read(assembly, contextName));
                }
            }
        }

        return assemblies;
    }

    private static LoadedAssembly Read(Assembly assembly, string contextName)
    {
        var name = assembly.GetName().Name ?? assembly.FullName ?? string.Empty;
        try
        {
            var build = BuildInformation.FromAssembly(assembly);
            return new LoadedAssembly(name, contextName, build.Version, build.Commit, build.RepositoryUrl);
        }
        catch (Exception exception) when (exception is TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException or CustomAttributeFormatException)
        {
            return new LoadedAssembly(name, contextName, string.Empty, null, null);
        }
    }

    private static bool IsSharedFrameworkAssembly(Assembly assembly) =>
        SharedFrameworkRoot is not null &&
        assembly.Location.StartsWith(SharedFrameworkRoot, StringComparison.OrdinalIgnoreCase);

    // The core library sits in <root>/shared/Microsoft.NETCore.App/<version>/; a self-contained app has no shared folder.
    private static string? GetSharedFrameworkRoot()
    {
        var versionDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var frameworkDirectory = versionDirectory is null ? null : Path.GetDirectoryName(versionDirectory);
        var sharedDirectory = frameworkDirectory is null ? null : Path.GetDirectoryName(frameworkDirectory);
        return sharedDirectory is not null && string.Equals(Path.GetFileName(sharedDirectory), "shared", StringComparison.OrdinalIgnoreCase)
            ? sharedDirectory + Path.DirectorySeparatorChar
            : null;
    }
}
