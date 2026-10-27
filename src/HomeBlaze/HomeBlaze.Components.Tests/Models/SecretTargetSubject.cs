using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Components.Tests.Models;

/// <summary>
/// A subject with a secret configuration property, a secret state property, a value whose text shows a secret, and a
/// plain value.
/// </summary>
[InterceptorSubject]
public partial class SecretTargetSubject
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration(IsSecret = true)]
    public partial string? Password { get; set; }

    [State, Configuration(IsSecret = true)]
    public partial string? Token { get; set; }

    [Configuration]
    public partial SecretCredentials? Credentials { get; set; }
}

public sealed record SecretCredentials(string User, [property: Configuration(IsSecret = true)] string Password);
