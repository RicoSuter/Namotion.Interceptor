using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Serialization;

[InterceptorSubject]
public partial class TestSubject : IConfigurableSubject
{
    [Configuration]
    public partial string ConfigProperty { get; set; }

    public partial string StateProperty { get; set; }

    public TestSubject()
    {
        ConfigProperty = string.Empty;
        StateProperty = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public class TestValueObject
{
    [Configuration]
    public string PropertyOne { get; set; } = string.Empty;

    [Configuration]
    public string PropertyTwo { get; set; } = string.Empty;
}

public class NestedValueObject
{
    [Configuration]
    public string SettingA { get; set; } = string.Empty;

    [Configuration]
    public string SettingB { get; set; } = string.Empty;
}

[InterceptorSubject]
public partial class ParentSubject : IConfigurableSubject
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial NestedValueObject? Config { get; set; }

    public ParentSubject()
    {
        Name = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class ParentWithChildSubject : IConfigurableSubject
{
    [Configuration]
    public partial string Name { get; set; }

    [Configuration]
    public partial ChildSubject? Child { get; set; }

    public ParentWithChildSubject()
    {
        Name = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class ChildSubject : IConfigurableSubject
{
    [Configuration]
    public partial string ChildConfig { get; set; }

    public partial string ChildState { get; set; }

    public ChildSubject()
    {
        ChildConfig = string.Empty;
        ChildState = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class SubjectWithList : IConfigurableSubject
{
    [Configuration]
    public partial List<ChildSubject> Items { get; set; }

    public SubjectWithList()
    {
        Items = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class SubjectWithDictionary : IConfigurableSubject
{
    [Configuration]
    public partial Dictionary<string, ChildSubject> Items { get; set; }

    public SubjectWithDictionary()
    {
        Items = new Dictionary<string, ChildSubject>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class SubjectWithValueObjectList : IConfigurableSubject
{
    [Configuration]
    public partial List<NestedValueObject> Configs { get; set; }

    public SubjectWithValueObjectList()
    {
        Configs = [];
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

// New test subjects for comprehensive coverage

/// <summary>
/// Subject with int-key dictionary (like Dictionary&lt;int, GpioPin&gt;)
/// </summary>
[InterceptorSubject]
public partial class SubjectWithIntKeyDictionary : IConfigurableSubject
{
    [Configuration]
    public partial Dictionary<int, ChildSubject> Items { get; set; }

    public SubjectWithIntKeyDictionary()
    {
        Items = new Dictionary<int, ChildSubject>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Deep nested subject for testing 3+ levels
/// </summary>
[InterceptorSubject]
public partial class Level1Subject : IConfigurableSubject
{
    [Configuration]
    public partial string Level1Config { get; set; }

    [Configuration]
    public partial Level2Subject? Child { get; set; }

    public Level1Subject()
    {
        Level1Config = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class Level2Subject : IConfigurableSubject
{
    [Configuration]
    public partial string Level2Config { get; set; }

    [Configuration]
    public partial Level3Subject? Child { get; set; }

    public Level2Subject()
    {
        Level2Config = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class Level3Subject : IConfigurableSubject
{
    [Configuration]
    public partial string Level3Config { get; set; }

    public Level3Subject()
    {
        Level3Config = string.Empty;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Subject with mixed Configuration and State properties
/// </summary>
[InterceptorSubject]
public partial class SubjectWithMixedProperties : IConfigurableSubject
{
    [Configuration]
    public partial string ConfigOne { get; set; }

    [Configuration]
    public partial int ConfigTwo { get; set; }

    public partial string StateOne { get; set; }

    public partial bool StateTwo { get; set; }

    public SubjectWithMixedProperties()
    {
        ConfigOne = string.Empty;
        ConfigTwo = 0;
        StateOne = string.Empty;
        StateTwo = false;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
