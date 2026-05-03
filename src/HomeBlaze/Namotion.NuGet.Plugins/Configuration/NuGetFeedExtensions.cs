using NuGet.Configuration;
using NuGet.Protocol.Core.Types;

namespace Namotion.NuGet.Plugins.Configuration;

internal static class NuGetFeedExtensions
{
    public static SourceRepository CreateSourceRepository(this NuGetFeed feed)
    {
        var packageSource = new PackageSource(feed.Url);
        if (feed.ApiKey != null)
        {
            packageSource.Credentials = new PackageSourceCredential(
                feed.Url, "apikey", feed.ApiKey, isPasswordClearText: true, validAuthenticationTypesText: null);
        }

        var providers = new List<Lazy<INuGetResourceProvider>>();
        providers.AddRange(global::NuGet.Protocol.Core.Types.Repository.Provider.GetCoreV3());
        return new SourceRepository(packageSource, providers);
    }
}
