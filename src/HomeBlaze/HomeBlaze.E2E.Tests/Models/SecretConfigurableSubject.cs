using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.E2E.Tests.Models;

/// <summary>A subject with a secret configuration property, whose value the storage keeps in the secret store.</summary>
[InterceptorSubject]
public partial class SecretConfigurableSubject : IConfigurable
{
    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
