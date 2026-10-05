using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Paths;

[InterceptorSubject]
public partial class TestContainer
{
    public partial string Name { get; set; }
    public partial Dictionary<string, TestItem> Items { get; set; }

    public TestContainer()
    {
        Items = new Dictionary<string, TestItem>();
    }
}

[InterceptorSubject]
public partial class TestItem
{
    public partial string Value { get; set; }
    public partial Dictionary<string, TestItem> Children { get; set; }

    public TestItem()
    {
        Children = new Dictionary<string, TestItem>();
    }
}

/// <summary>
/// Test model with [InlinePaths] — dictionary keys become direct path segments.
/// </summary>
[InterceptorSubject]
public partial class TestInlineContainer
{
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestInlineContainer> Children { get; set; }

    public TestInlineContainer()
    {
        Children = new Dictionary<string, TestInlineContainer>();
    }
}

/// <summary>
/// Test model whose dictionary property is declared broadly enough to hold any dictionary
/// implementation, including <see cref="System.Collections.Immutable.ImmutableDictionary{TKey,TValue}"/>.
/// </summary>
[InterceptorSubject]
public partial class TestBroadContainer
{
    public partial string Name { get; set; }
    public partial IReadOnlyDictionary<string, TestItem> Items { get; set; }

    public TestBroadContainer()
    {
        Items = new Dictionary<string, TestItem>();
    }
}

[InterceptorSubject]
public partial class TestDoubleKeyedContainer
{
    public partial Dictionary<double, TestItem> Items { get; set; }

    public TestDoubleKeyedContainer()
    {
        Items = new Dictionary<double, TestItem>();
    }
}

public enum TestColor
{
    Red = 1,
    Green = 2,
    Blue = 4
}

[Flags]
public enum TestPermissions
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4
}

[InterceptorSubject]
public partial class TestKeyedContainer
{
    public partial Dictionary<int, TestItem> ByNumber { get; set; }
    public partial Dictionary<TestColor, TestItem> ByColor { get; set; }
    public partial Dictionary<TestPermissions, TestItem> ByPermissions { get; set; }
    public partial Dictionary<Guid, TestItem> ById { get; set; }
    public partial Dictionary<object, TestItem> ByAnything { get; set; }
    public partial IReadOnlyDictionary<int, TestItem> ReadOnlyByNumber { get; set; }
    public partial List<TestItem> Items { get; set; }

    public TestKeyedContainer()
    {
        ByNumber = new Dictionary<int, TestItem>();
        ByColor = new Dictionary<TestColor, TestItem>();
        ByPermissions = new Dictionary<TestPermissions, TestItem>();
        ById = new Dictionary<Guid, TestItem>();
        ByAnything = new Dictionary<object, TestItem>();
        ReadOnlyByNumber = new Dictionary<int, TestItem>();
        Items = new List<TestItem>();
    }
}

[InterceptorSubject]
public partial class TestNumberedInlineContainer
{
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<int, TestNumberedInlineContainer> Children { get; set; }

    public TestNumberedInlineContainer()
    {
        Children = new Dictionary<int, TestNumberedInlineContainer>();
    }
}

[InterceptorSubject]
public partial class TestMappedInlineContainer
{
    [Path("test", "name")]
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestMappedInlineContainer> Children { get; set; }

    public TestMappedInlineContainer()
    {
        Children = new Dictionary<string, TestMappedInlineContainer>();
    }
}

[InterceptorSubject]
public partial class TestShadowedInlineContainer
{
    [Path("test", "Children")]
    public partial string Label { get; set; }

    [Path("test", "name")]
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestShadowedInlineContainer> Children { get; set; }

    public TestShadowedInlineContainer()
    {
        Children = new Dictionary<string, TestShadowedInlineContainer>();
    }
}
