using Namotion.Interceptor.ConnectorTester.Hosting;

var profile = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
var runDirectory = Path.Combine("logs", $"{DateTime.UtcNow:yyyy-MM-ddTHH-mm-ssZ}-{profile}");
Directory.CreateDirectory(runDirectory);

var connectorTesterHost = ConnectorTesterHost.Build(args, runDirectory);

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
