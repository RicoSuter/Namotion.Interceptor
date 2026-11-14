using HomeBlaze.Storage.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Installation.Tests.Testing;

/// <summary>
/// A root that holds the installation and records the configurations written through it, as a storage does.
/// </summary>
[InterceptorSubject]
public partial class TestRoot : IConfigurationWriter
{
    private readonly Lock _lock = new();
    private readonly List<IInterceptorSubject> _written = [];

    public partial HomeBlazeInstallation? Installation { get; set; }

    public partial TestDevice? Device { get; set; }

    public TestRoot()
    {
        Installation = null;
        Device = null;
    }

    public IReadOnlyList<IInterceptorSubject> Written
    {
        get
        {
            lock (_lock)
            {
                return [.. _written];
            }
        }
    }

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _written.Add(subject);
        }

        return Task.FromResult(true);
    }
}
