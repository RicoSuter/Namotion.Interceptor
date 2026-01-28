using Xunit.Extensions.AssemblyFixture;

// Register the assembly fixture framework for shared OPC UA server.
// This enables parallel test execution while sharing a single server instance.
[assembly: TestFramework(AssemblyFixtureFramework.TypeName, AssemblyFixtureFramework.AssemblyName)]
