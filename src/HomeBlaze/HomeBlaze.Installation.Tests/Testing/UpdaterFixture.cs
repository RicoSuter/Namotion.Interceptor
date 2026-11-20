using HomeBlaze.Abstractions;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;

namespace HomeBlaze.Installation.Tests.Testing;

/// <summary>
/// An installation attached to a context whose data directory is a temporary folder, and the moves of the updater
/// sidecar, done by hand so a test decides when each happens.
/// </summary>
internal sealed class UpdaterFixture : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly IInterceptorSubjectContext _context;
    private readonly DataDirectoryProvider _dataDirectoryProvider;

    public UpdaterFixture(BuildInformation? build = null, TimeProvider? timeProvider = null)
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("updater-");
        TimeProvider = timeProvider ?? new TestTimeProvider(Start);
        TestTimeProvider = TimeProvider as TestTimeProvider;

        _context = SubjectContextFactory.Create(new ServiceCollection());
        _dataDirectoryProvider = new DataDirectoryProvider(Directory.FullName);
        _context.AddService<IDataDirectoryProvider>(_dataDirectoryProvider);
        Installation = Attach(build ?? HomeBlazeInstallationTests.CreateBuild());
    }

    public DirectoryInfo Directory { get; }

    public TimeProvider TimeProvider { get; }

    /// <summary>The clock when the fixture owns a test clock; null with the system clock.</summary>
    public TestTimeProvider? TestTimeProvider { get; }

    public HomeBlazeInstallation Installation { get; }

    public string PathOf(string fileName) => Path.Combine(Directory.FullName, fileName);

    public void TouchHeartbeat()
    {
        File.WriteAllText(PathOf(UpdaterFiles.HeartbeatFileName), string.Empty);
        File.SetLastWriteTimeUtc(PathOf(UpdaterFiles.HeartbeatFileName), TimeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>Meets every condition of InstallUpdate_IsEnabled.</summary>
    public void MakeReady()
    {
        TouchHeartbeat();
        Installation.RefreshUpdaterAvailability();
        Installation.Status = ServiceStatus.Running;
        Installation.UpdateAvailable = true;
    }

    /// <summary>The sidecar takes the request, as its mv does.</summary>
    public void PickUp() =>
        File.Move(PathOf(UpdaterFiles.RequestFileName), PathOf(UpdaterFiles.RunningFileName));

    /// <summary>The sidecar finishes compose and writes its output and exit code.</summary>
    public void Finish(int exitCode, string log)
    {
        File.WriteAllText(PathOf(UpdaterFiles.LogFileName), log);

        // Written through a rename, as the sidecar does, so the monitor never reads a partial result.
        var temporaryPath = PathOf(UpdaterFiles.ResultFileName + ".tmp");
        File.WriteAllText(temporaryPath, $"{exitCode}\n");
        File.Move(temporaryPath, PathOf(UpdaterFiles.ResultFileName), overwrite: true);
    }

    /// <summary>The next read of the data directory throws, as a broken data folder would; later reads succeed.</summary>
    public void FailNextDataDirectoryAccess() => _dataDirectoryProvider.FailNextAccess = true;

    /// <summary>
    /// The process that the recreated container starts, sharing the data folder and the clock.
    /// </summary>
    public HomeBlazeInstallation CreateSecondInstallation(BuildInformation build) => Attach(build);

    public void Dispose() => Directory.Delete(recursive: true);

    private HomeBlazeInstallation Attach(BuildInformation build)
    {
        var root = new TestRoot(_context);
        var installation = HomeBlazeInstallationTests.CreateInstallation(build, timeProvider: TimeProvider);
        root.Installation = installation;
        return installation;
    }

    private sealed class DataDirectoryProvider(string directory) : IDataDirectoryProvider
    {
        public bool FailNextAccess { get; set; }

        public string DataDirectory
        {
            get
            {
                if (FailNextAccess)
                {
                    FailNextAccess = false;
                    throw new IOException("The data directory is not available");
                }

                return directory;
            }
        }
    }
}
