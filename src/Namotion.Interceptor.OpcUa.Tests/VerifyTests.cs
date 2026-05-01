using Namotion.Interceptor.OpcUa.Client;
using PublicApiGenerator;

namespace Namotion.Interceptor.OpcUa.Tests
{
    public class VerifyChecksTests
    {
        [Fact]
        public Task Run() => VerifyChecks.Run();

        /// <summary>
        /// Snapshot of the assembly's public API. When this fails after an intentional API change,
        /// review the diff and accept by replacing the .verified.txt file with the test's .received.txt.
        /// </summary>
        [Fact]
        public Task PublicApi() => Verify(typeof(IOpcUaSubjectClientSource).Assembly.GeneratePublicApi());
    }
}
