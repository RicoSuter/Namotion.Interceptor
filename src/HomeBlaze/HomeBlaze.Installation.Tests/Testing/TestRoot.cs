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

    /// <summary>The refusal that writes throw, and also the check unless <see cref="IsRefusalFoundOnlyByWrite"/>.</summary>
    public ConfigurationWriteRefusedException? Refusal { get; set; }

    /// <summary>Whether only a write throws <see cref="Refusal"/>, as when the file was read again after the check.</summary>
    public bool IsRefusalFoundOnlyByWrite { get; set; }

    /// <summary>Runs at the start of each write, as something that happens while the storage writes.</summary>
    public Action? DuringWrite { get; set; }

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        DuringWrite?.Invoke();
        if (Refusal is { } refusal)
        {
            throw refusal;
        }

        lock (_lock)
        {
            _written.Add(subject);
        }

        return Task.FromResult(true);
    }

    public void ThrowIfWriteRefused(IInterceptorSubject subject)
    {
        if (Refusal is { } refusal && !IsRefusalFoundOnlyByWrite)
        {
            throw refusal;
        }
    }
}
