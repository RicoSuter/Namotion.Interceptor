using BenchmarkDotNet.Attributes;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Paths;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

[InterceptorSubject]
public partial class BatchRoot
{
    public BatchRoot()
    {
        Device = new BatchDevice();
    }

    public partial BatchDevice Device { get; set; }
}

[InterceptorSubject]
public partial class BatchDevice
{
    public BatchDevice()
    {
        Settings = new BatchSettings();
    }

    public partial BatchSettings Settings { get; set; }
}

[InterceptorSubject]
public partial class BatchSettings
{
    public partial int Value00 { get; set; }
    public partial int Value01 { get; set; }
    public partial int Value02 { get; set; }
    public partial int Value03 { get; set; }
    public partial int Value04 { get; set; }
    public partial int Value05 { get; set; }
    public partial int Value06 { get; set; }
    public partial int Value07 { get; set; }
    public partial int Value08 { get; set; }
    public partial int Value09 { get; set; }
    public partial int Value10 { get; set; }
    public partial int Value11 { get; set; }
    public partial int Value12 { get; set; }
    public partial int Value13 { get; set; }
    public partial int Value14 { get; set; }
    public partial int Value15 { get; set; }
    public partial int Value16 { get; set; }
    public partial int Value17 { get; set; }
    public partial int Value18 { get; set; }
    public partial int Value19 { get; set; }
}

#pragma warning disable CS8618

/// <summary>
/// Compares resolving a batch of source paths that share a two-level prefix (<c>Device.Settings.ValueNN</c>)
/// against a batch of paths with no shared prefix (<c>ValueNN</c> directly on the leaf subject).
/// </summary>
[MemoryDiagnoser]
public class PathBatchBenchmark
{
    private IInterceptorSubject _root;
    private IInterceptorSubject _settings;
    private PathProviderBase _pathProvider;
    private string[] _sharedPrefixPaths;
    private string[] _singleLevelPaths;

    [GlobalSetup]
    public void Setup()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        var root = new BatchRoot(context);

        _root = root;
        _settings = root.Device.Settings;
        _pathProvider = DefaultPathProvider.Instance;

        _sharedPrefixPaths = Enumerable.Range(0, 20)
            .Select(i => $"Device.Settings.Value{i:D2}")
            .ToArray();

        _singleLevelPaths = Enumerable.Range(0, 20)
            .Select(i => $"Value{i:D2}")
            .ToArray();
    }

    [Benchmark]
    public List<(string path, RegisteredSubjectProperty? property, object? index)> GetPropertiesFromPaths_SharedPrefix()
        => _root.GetPropertiesFromPaths(_sharedPrefixPaths, _pathProvider).ToList();

    [Benchmark]
    public List<(string path, RegisteredSubjectProperty? property, object? index)> GetPropertiesFromPaths_SingleLevel()
        => _settings.GetPropertiesFromPaths(_singleLevelPaths, _pathProvider).ToList();
}
