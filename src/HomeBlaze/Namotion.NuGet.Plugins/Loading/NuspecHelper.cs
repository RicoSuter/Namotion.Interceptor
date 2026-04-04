using System.Xml.Linq;
using NuGet.Packaging;

namespace Namotion.NuGet.Plugins.Loading;

/// <summary>
/// Reads nuspec files from extracted NuGet package directories.
/// </summary>
internal static class NuspecHelper
{
    /// <summary>
    /// Reads the nuspec from the given extracted package directory.
    /// Returns metadata and the raw XDocument. If no nuspec is found, returns default metadata and null XDocument.
    /// </summary>
    public static (NuGetPackageMetadata Metadata, XDocument? Nuspec) ReadFromExtractedPackage(string extractedPackagePath)
    {
        var nuspecFile = Directory.GetFiles(extractedPackagePath, "*.nuspec").FirstOrDefault();
        if (nuspecFile == null)
        {
            return (new NuGetPackageMetadata(), null);
        }

        var bytes = File.ReadAllBytes(nuspecFile);

        NuGetPackageMetadata metadata;
        using (var stream = new MemoryStream(bytes))
        {
            var nuspecReader = new NuspecReader(stream);

            var tags = nuspecReader.GetTags()?
                .Split([' '], StringSplitOptions.RemoveEmptyEntries)
                .ToList() ?? [];

            metadata = new NuGetPackageMetadata
            {
                Title = nuspecReader.GetTitle() ?? "",
                Description = nuspecReader.GetDescription() ?? "",
                Authors = nuspecReader.GetAuthors() ?? "",
                IconUrl = nuspecReader.GetIconUrl()?.ToString(),
                ProjectUrl = nuspecReader.GetProjectUrl()?.ToString(),
                LicenseUrl = nuspecReader.GetLicenseUrl()?.ToString(),
                Tags = tags,
            };
        }

        XDocument xdocument;
        using (var stream = new MemoryStream(bytes))
        {
            xdocument = XDocument.Load(stream);
        }

        return (metadata, xdocument);
    }
}
