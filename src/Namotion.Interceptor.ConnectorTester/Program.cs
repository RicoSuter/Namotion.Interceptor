using Namotion.Interceptor.ConnectorTester.Hosting;

var connectorTesterHost = ConnectorTesterHost.Build(args);

try
{
    await connectorTesterHost.Host.RunAsync();
}
finally
{
    // Bug fix #4: dispose profilers even when RunAsync throws.
    foreach (var profiler in connectorTesterHost.Profilers)
    {
        profiler.Dispose();
    }
}

if (connectorTesterHost.VerificationEngine is { Failed: true })
{
    Environment.ExitCode = 1;
}
