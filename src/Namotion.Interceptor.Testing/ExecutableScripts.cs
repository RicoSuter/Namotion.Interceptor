using System.Diagnostics;
using System.Runtime.Versioning;

namespace Namotion.Interceptor.Testing;

/// <summary>
/// Writes executable scripts that tests start as processes while other tests start processes in parallel.
/// </summary>
public static class ExecutableScripts
{
    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> as a file only its owner can read, write and execute.
    /// </summary>
    /// <exception cref="InvalidOperationException">The script could not be written.</exception>
    [UnsupportedOSPlatform("windows")]
    public static void Write(string path, string content)
    {
        // A process started by a parallel test while this one holds the script open for writing inherits that handle
        // until it executes its own program, and running the script then fails with "Text file busy". The script is
        // therefore written by a child process, whose handles no other process inherits; this process only writes a
        // template.
        var templatePath = path + ".template";
        File.WriteAllText(templatePath, content);
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var argument in new[] { "-c", "cp \"$1\" \"$2\" && chmod 700 \"$2\"", "sh", templatePath, path })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The script could not be written to {path}.");
        }
    }
}
