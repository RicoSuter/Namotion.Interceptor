using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Models;

/// <summary>
/// Test model with a list of subjects that are all equal to each other, so only a comparison by reference tells them apart.
/// </summary>
[InterceptorSubject]
public partial class TestEqualItemList
{
    public partial List<TestEqualItem> Items { get; set; }

    public TestEqualItemList()
    {
        Items = [];
    }
}

[InterceptorSubject]
public partial class TestEqualItem
{
    public override bool Equals(object? obj) => obj is TestEqualItem;

    public override int GetHashCode() => 0;
}
