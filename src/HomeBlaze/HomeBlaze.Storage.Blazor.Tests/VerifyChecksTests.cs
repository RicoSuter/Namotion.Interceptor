using HomeBlaze.Storage.Blazor.Models;
using PublicApiGenerator;

namespace HomeBlaze.Storage.Blazor.Tests;

public class VerifyChecksTests
{
    [Fact]
    public Task Run() => VerifyChecks.Run();

    /// <summary>
    /// Snapshot of the assembly's public API. When this fails after an intentional API change,
    /// review the diff and accept by replacing the .verified.txt file with the test's .received.txt.
    /// </summary>
    [Fact]
    public Task PublicApi() => Verify(typeof(BlockOutline).Assembly.GeneratePublicApi(new ApiGeneratorOptions
    {
        DenyNamespacePrefixes = ["System", "XamlGeneratedNamespace"]
    }));
}
