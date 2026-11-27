using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace HomeBlaze.App;

/// <summary>Opens links the page hands to the shell in the user's default browser or mail client.</summary>
public static class ExternalLinks
{
    /// <summary>Prefix of the web message that asks the shell to open the URL that follows it.</summary>
    public const string MessagePrefix = "shell:open-external:";

    /// <summary>
    /// Parses an open-external web message. Returns false when the message is not one, or when its URL is
    /// not one <see cref="TryCreate"/> accepts.
    /// </summary>
    public static bool TryParseMessage(string message, [NotNullWhen(true)] out Uri? uri)
    {
        if (!message.StartsWith(MessagePrefix, StringComparison.Ordinal))
        {
            uri = null;
            return false;
        }

        return TryCreate(message[MessagePrefix.Length..], out uri);
    }

    /// <summary>
    /// Accepts only absolute <c>http</c> and <c>https</c> URLs with a host, and <c>mailto</c> URLs.
    /// Everything else, such as <c>file</c>, <c>javascript</c> or a custom protocol, is rejected, because
    /// the system opener would hand it to whatever application claims that scheme.
    /// </summary>
    public static bool TryCreate(string? candidate, [NotNullWhen(true)] out Uri? uri)
    {
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) &&
            (((parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps) && parsed.Host.Length > 0) ||
             parsed.Scheme == Uri.UriSchemeMailto))
        {
            uri = parsed;
            return true;
        }

        uri = null;
        return false;
    }

    /// <summary>Builds the process start that opens <paramref name="uri"/> with the platform's opener.</summary>
    /// <remarks>The URL is passed as a single argument, never through a command line a shell would parse.</remarks>
    public static ProcessStartInfo CreateStartInfo(Uri uri, OSPlatform platform)
    {
        var url = uri.AbsoluteUri;
        if (platform == OSPlatform.Windows)
        {
            return new ProcessStartInfo(url) { UseShellExecute = true };
        }

        var startInfo = new ProcessStartInfo(platform == OSPlatform.OSX ? "open" : "xdg-open")
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    /// <summary>
    /// Opens <paramref name="uri"/> on a thread-pool thread, so a slow opener never stalls the window's
    /// message loop. A failure, such as a missing <c>xdg-open</c>, is traced and otherwise ignored.
    /// </summary>
    public static void OpenInBackground(Uri uri)
    {
        var platform = OperatingSystem.IsWindows() ? OSPlatform.Windows
            : OperatingSystem.IsMacOS() ? OSPlatform.OSX
            : OSPlatform.Linux;

        _ = Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(CreateStartInfo(uri, platform));
            }
            catch (Exception exception)
            {
                Trace.TraceWarning($"Could not open '{uri.AbsoluteUri}' externally: {exception.Message}");
            }
        });
    }
}
