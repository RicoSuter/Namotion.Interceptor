using System.Collections.Immutable;
using HomeBlaze.Storage.Abstractions.Metadata;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>A folder that holds its children by name and keeps their order in its metadata.</summary>
[InterceptorSubject]
public partial class PermissionsTestFolder : IMetadataContainer
{
    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }

    public PermissionsTestFolder()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    public ImmutableArray<string> ChildOrder => [];
}
