using Namotion.Interceptor.ConnectorTester.Hosting;

var connectorTesterHost = ConnectorTesterHost.Build(args);

await connectorTesterHost.Host.RunAsync();

if (connectorTesterHost.VerificationEngine is { Failed: true })
{
    Environment.ExitCode = 1;
}
