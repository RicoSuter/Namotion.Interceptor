using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Abstractions.Metadata;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.AI.Tests.Mcp;

[InterceptorSubject]
public partial class TestThing : ITitleProvider, IIconProvider
{
    public string? Title => Name;
    public string? IconName => "Lightbulb";

    [State]
    public partial string Name { get; set; }

    [State]
    public partial decimal Temperature { get; set; }

    public partial TestChildThing? Child { get; set; }
}

[InterceptorSubject]
public partial class TestChildThing : ITitleProvider
{
    public string? Title => ChildName;

    [State]
    public partial string ChildName { get; set; }

    [State]
    public partial bool IsActive { get; set; }
}

[InterceptorSubject]
public partial class TestFolder : ITitleProvider
{
    public string? Title => Name;

    [State]
    public partial string Name { get; set; }

    [InlinePaths]
    public partial Dictionary<string, TestFolder> Children { get; set; }

    public TestFolder()
    {
        Children = new Dictionary<string, TestFolder>();
    }
}

public interface ITestSensorInterface
{
    decimal Temperature { get; }
}
