using PublicApiGenerator;

namespace Namotion.Interceptor.Mqtt.Tests
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
        public Task PublicApi() => Verify(typeof(MqttConstants).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            DenyNamespacePrefixes = ["System", "XamlGeneratedNamespace"]
        }));
    }
}
