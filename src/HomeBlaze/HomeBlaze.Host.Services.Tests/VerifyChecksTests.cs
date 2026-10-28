using HomeBlaze.Host.Services.Display;
using PublicApiGenerator;

namespace HomeBlaze.Host.Services.Tests;

public class VerifyChecksTests
{
    /// <summary>
    /// Snapshot of the assembly's public API. When this fails after an intentional API change,
    /// review the diff and accept by replacing the .verified.txt file with the test's .received.txt.
    /// </summary>
    [Fact]
    public Task PublicApi() => Verify(typeof(SubjectDisplayExtensions).Assembly.GeneratePublicApi(new ApiGeneratorOptions
    {
        DenyNamespacePrefixes = ["System", "XamlGeneratedNamespace"]
    }));
}
