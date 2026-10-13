using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HomeBlaze.AI.Agents.Cli;

/// <summary>
/// Writes files that only the user HomeBlaze runs as may read, for content such as turn tokens and agent memory.
/// </summary>
internal static class OwnerOnlyFile
{
    /// <summary>Creates the file, which must not exist, readable and writable by the current user only, and writes the content.</summary>
    public static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        await using var stream = OperatingSystem.IsWindows()
            ? CreateOnWindows(path)
            : new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), cancellationToken);
    }

    // Without protection the file would inherit the entries of its directory, which may grant other users access.
    [SupportedOSPlatform("windows")]
    private static FileStream CreateOnWindows(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, bufferSize: 4096, FileOptions.None, security);
    }
}
