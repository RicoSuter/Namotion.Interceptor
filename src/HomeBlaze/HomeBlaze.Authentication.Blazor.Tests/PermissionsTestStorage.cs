using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A storage with an operation that requires administration and one that does not.</summary>
[InterceptorSubject]
public partial class PermissionsTestStorage
{
    [Operation(RequiresAdministration = true)]
    public Task CreateAsync() => Task.CompletedTask;

    [Operation]
    public Task RefreshAsync() => Task.CompletedTask;
}
