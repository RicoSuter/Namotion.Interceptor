using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Serialization;

[InterceptorSubject]
public partial class TestSubject
{
    [Configuration]
    public partial string ConfigProperty { get; set; }

    public partial string StateProperty { get; set; }

    public TestSubject()
    {
        ConfigProperty = string.Empty;
        StateProperty = string.Empty;
    }
}

public class TestValueObject
{
    public string PropertyOne { get; set; } = string.Empty;
    public string PropertyTwo { get; set; } = string.Empty;
}

public class NestedValueObject
{
    public string SettingA { get; set; } = string.Empty;
    public string SettingB { get; set; } = string.Empty;
}

[InterceptorSubject]
public partial class ParentSubject
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial NestedValueObject? Config { get; set; }

    public ParentSubject()
    {
        Name = string.Empty;
    }
}

[InterceptorSubject]
public partial class ParentWithChildSubject
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial ChildSubject? Child { get; set; }

    public ParentWithChildSubject()
    {
        Name = string.Empty;
    }
}

[InterceptorSubject]
public partial class ChildSubject
{
    [Configuration]
    public partial string ChildConfig { get; set; }

    public partial string ChildState { get; set; }

    public ChildSubject()
    {
        ChildConfig = string.Empty;
        ChildState = string.Empty;
    }
}

[InterceptorSubject]
public partial class SubjectWithList
{
    [Configuration]
    public partial List<ChildSubject> Items { get; set; }

    public SubjectWithList()
    {
        Items = [];
    }
}

[InterceptorSubject]
public partial class SubjectWithDictionary
{
    [Configuration]
    public partial Dictionary<string, ChildSubject> Items { get; set; }

    public SubjectWithDictionary()
    {
        Items = new Dictionary<string, ChildSubject>();
    }
}

[InterceptorSubject]
public partial class SubjectWithValueObjectList
{
    [Configuration]
    public partial List<NestedValueObject> Configs { get; set; }

    public SubjectWithValueObjectList()
    {
        Configs = [];
    }
}
