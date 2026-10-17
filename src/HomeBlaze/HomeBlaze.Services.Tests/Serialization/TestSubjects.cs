using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Serialization;

[InterceptorSubject]
public partial class TestSubject : IConfigurable
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
public partial class ParentSubject : IConfigurable
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
public partial class ParentWithChildSubject : IConfigurable
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
public partial class ChildSubject : IConfigurable
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
public partial class SubjectWithList : IConfigurable
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
public partial class SubjectWithDictionary : IConfigurable
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
public partial class SubjectWithValueObjectList : IConfigurable
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
public partial class SubjectWithIntKeyDictionary : IConfigurable
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
public partial class Level1Subject : IConfigurable
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
public partial class Level2Subject : IConfigurable
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
public partial class Level3Subject : IConfigurable
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
public partial class SubjectWithMixedProperties : IConfigurable
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

public sealed class TestConstructorDependency;

/// <summary>
/// Subject that can only be created with a dependency, like a device that needs a logger.
/// </summary>
[InterceptorSubject]
public partial class SubjectWithConstructorDependency : IConfigurable
{
    private readonly TestConstructorDependency _dependency;

    public SubjectWithConstructorDependency(TestConstructorDependency dependency)
    {
        _dependency = dependency;
        Config = string.Empty;
    }

    // A method, because System.Text.Json would bind the constructor parameter to a public property of
    // that name and pass null instead of failing the way a subject with a private dependency does.
    public TestConstructorDependency GetDependency() => _dependency;

    [Configuration]
    public partial string Config { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Subject that hosts a child of any configurable type, like a grid cell.
/// </summary>
[InterceptorSubject]
public partial class SubjectWithAnyChild : IConfigurable
{
    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Subject whose configuration property rejects a value outside its range in the setter, after the constructor ran.
/// </summary>
[InterceptorSubject]
public partial class SubjectWithValidatedProperty : IConfigurable
{
    [Configuration]
    [Range(0, 10)]
    public partial int Level { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A subject that is not configurable, so the serializer cannot write it.</summary>
[InterceptorSubject]
public partial class PlainSubject;

/// <summary>Subject whose configuration property throws when it is read, so it cannot be serialized.</summary>
[InterceptorSubject]
public partial class SubjectWithUnreadableConfiguration : IConfigurable
{
    [Configuration]
    public string Value
    {
        get => throw new InvalidOperationException("The value cannot be read.");
        set { }
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
