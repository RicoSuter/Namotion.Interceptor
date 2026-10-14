using System.ComponentModel.DataAnnotations;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Services;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.Storage.Tests.Pages;

[InterceptorSubject]
public partial class TestLeaf : IConfigurable
{
    [Configuration]
    public partial string? Name { get; set; }

    [Configuration]
    public partial int Count { get; set; }

    [Configuration]
    public partial bool Enabled { get; set; }

    [Configuration]
    public partial TimeSpan Interval { get; set; }

    [Configuration]
    public partial List<string>? Tags { get; set; }

    public int ApplyCount { get; private set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ApplyCount++;
        return Task.CompletedTask;
    }
}

/// <summary>Fails when its configuration is applied, depending on <see cref="Mode"/>.</summary>
[InterceptorSubject]
public partial class TestFaulty : IConfigurable
{
    public const string ThrowMode = "throw";
    public const string CancelMode = "cancel";

    [Configuration]
    public partial string? Mode { get; set; }

    /// <summary>
    /// Cancelled before <see cref="CancelMode"/> throws. With it the cancellation is the caller's,
    /// without it the subject cancels on its own.
    /// </summary>
    public CancellationTokenSource? CancellationSource { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        switch (Mode)
        {
            case ThrowMode:
                throw new InvalidOperationException("apply failed");

            case CancelMode:
                CancellationSource?.Cancel();
                throw new TaskCanceledException("apply cancelled");

            default:
                return Task.CompletedTask;
        }
    }
}

[InterceptorSubject]
public partial class TestThrowingConstructor : IConfigurable
{
    public TestThrowingConstructor()
    {
        throw new InvalidOperationException("constructor failed");
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public abstract class TestAbstractConfigurable : IConfigurable
{
    public abstract Task ApplyConfigurationAsync(CancellationToken cancellationToken = default);
}

public interface ITestConfigurable : IConfigurable;

[InterceptorSubject]
public partial class TestPanel : IConfigurable
{
    public TestPanel()
    {
        Rows = [];
        Items = [];
    }

    [Configuration]
    public partial string? Title { get; set; }

    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    [Slot]
    public partial IInterceptorSubject? Footer { get; set; }

    [Slot("Row")]
    public partial TestRow[] Rows { get; set; }

    [Slot("Item")]
    public partial IInterceptorSubject[] Items { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class TestRow : IConfigurable
{
    [Configuration]
    public partial string? Label { get; set; }

    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Content { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A subject that is not configurable, so a slot of this type cannot create its value.</summary>
[InterceptorSubject]
public partial class TestPlain;

/// <summary>Has slots whose values fail when they are created or configured.</summary>
[InterceptorSubject]
public partial class TestFaultyHost : IConfigurable
{
    public TestFaultyHost()
    {
        FaultyValues = [];
    }

    [Slot("Faulty")]
    public partial TestFaulty[] FaultyValues { get; set; }

    [Slot]
    public partial TestThrowingConstructor? Broken { get; set; }

    [Slot]
    public partial TestPlain? Plain { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class TestInvalidSlots : IConfigurable
{
    private static int _createdCount;

    public TestInvalidSlots()
    {
        Interlocked.Increment(ref _createdCount);
    }

    /// <summary>How many instances were ever created. A type with invalid slots must not be created at all.</summary>
    public static int CreatedCount => Volatile.Read(ref _createdCount);

    [Slot]
    public partial string? Text { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Clearing its slot fails in the setter, because the validation rejects null.</summary>
[InterceptorSubject]
public partial class TestRequiredSlotHost : IConfigurable
{
    [Slot]
    [Required]
    public partial IInterceptorSubject? Needed { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Holds a reload inside <see cref="ApplyConfigurationAsync"/> until the test lets it go on.</summary>
[InterceptorSubject]
public partial class TestGate : IConfigurable
{
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>Completes when the configuration is being applied.</summary>
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Awaited while the configuration is applied. Null lets it pass.</summary>
    public Task? Gate { get; set; }

    public async Task ApplyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        if (Gate is { } gate)
        {
            await gate;
        }
    }
}

[InterceptorSubject]
public partial class TwoDefaults
{
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? First { get; set; }

    [Slot(IsDefault = true)]
    public partial IInterceptorSubject? Second { get; set; }
}

[InterceptorSubject]
public partial class TypedDefault
{
    [Slot(IsDefault = true)]
    public partial TestRow? Content { get; set; }
}

[InterceptorSubject]
public partial class DottedName
{
    [Slot("A.B")]
    public partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class HyphenatedName
{
    [Slot("My-Item")]
    public partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class NameWithLeadingDigit
{
    [Slot("1st")]
    public partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class NameWithSpace
{
    [Slot("My Item")]
    public partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class NameWithUnderscoreAndDigit
{
    [Slot("_Item_2")]
    public partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class DuplicateNames
{
    [Slot("Item")]
    public partial IInterceptorSubject? First { get; set; }

    [Slot("Item")]
    public partial IInterceptorSubject? Second { get; set; }
}

[InterceptorSubject]
public partial class EmptyName
{
    [Slot("")]
    public partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class WhitespaceName
{
    [Slot(" ")]
    public partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class StringSlot
{
    [Slot]
    public partial string? Content { get; set; }
}

[InterceptorSubject]
public partial class ArrayDefault
{
    [Slot(IsDefault = true)]
    public partial IInterceptorSubject[]? Content { get; set; }
}

public interface ITestSlotValue : IInterceptorSubject;

[InterceptorSubject]
public partial class InterfaceSlot
{
    [Slot]
    public partial ITestSlotValue? Content { get; set; }
}

public abstract class TestAbstractRow : TestRow;

[InterceptorSubject]
public partial class AbstractSlot
{
    [Slot("Row")]
    public partial TestAbstractRow[]? Rows { get; set; }
}

[InterceptorSubject]
public partial class TestSlotBase
{
    [Slot(IsDefault = true)]
    public virtual partial IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class TestSlotDerived : TestSlotBase
{
    public override partial IInterceptorSubject? Content { get; set; }
}

/// <summary>A subject whose slot is an ordinary property, so a write to it is not tracked.</summary>
[InterceptorSubject]
public partial class NotInterceptedSlot
{
    [Slot]
    public IInterceptorSubject? Content { get; set; }
}

/// <summary>Not a subject class at all.</summary>
public class PlainSlotHost
{
    [Slot]
    public IInterceptorSubject? Content { get; set; }
}

[InterceptorSubject]
public partial class NoSetterSlot
{
    [Slot]
    public partial IInterceptorSubject? Content { get; }
}

/// <summary>Counts the <see cref="TestCountedChild"/> subjects that were created with it. One fixture has one.</summary>
public sealed class TestCreationCounter
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);
}

/// <summary>A subject in the configuration of another one that tells how often it was created.</summary>
[InterceptorSubject]
public partial class TestCountedChild : IConfigurable
{
    public TestCountedChild(TestCreationCounter counter)
    {
        counter.Increment();
    }

    [Configuration]
    public partial string? Name { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Holds subjects in its configuration, like a layout that holds a widget per cell.</summary>
[InterceptorSubject]
public partial class TestChildHost : IConfigurable
{
    public TestChildHost()
    {
        Children = [];
    }

    [Configuration]
    public partial int Count { get; set; }

    [Configuration]
    public partial List<IInterceptorSubject> Children { get; set; }

    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Resolves a path in page scope from a derived property, like a widget that shows another subject.</summary>
[InterceptorSubject]
public partial class TestReference : IConfigurable
{
    private readonly SubjectPathResolver _pathResolver;

    public TestReference(SubjectPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
        Path = string.Empty;
    }

    [Configuration]
    public partial string Path { get; set; }

    [Derived]
    public IInterceptorSubject? Resolved => _pathResolver.ResolveScopedSubject(Path, PathStyle.Canonical, this);

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// Records the <see cref="TestHosted"/> subjects of one context that were started and which of them still run.
/// A subject finds it as a service of its context.
/// </summary>
public sealed class TestHostedLog
{
    private readonly Lock _lock = new();
    private readonly List<TestHosted> _running = [];
    private int _startCount;

    /// <summary>How many times a subject was started.</summary>
    public int StartCount
    {
        get
        {
            lock (_lock)
            {
                return _startCount;
            }
        }
    }

    /// <summary>The subjects that were started and not stopped since.</summary>
    public IReadOnlyList<TestHosted> Running
    {
        get
        {
            lock (_lock)
            {
                return _running.ToArray();
            }
        }
    }

    public void Started(TestHosted subject)
    {
        lock (_lock)
        {
            _startCount++;
            _running.Add(subject);
        }
    }

    public void Stopped(TestHosted subject)
    {
        lock (_lock)
        {
            _running.Remove(subject);
        }
    }
}

/// <summary>A subject with a hosted service, like a device that polls while it is on a page.</summary>
[InterceptorSubject]
public partial class TestHosted : IHostedService, IConfigurable
{
    public const string ThrowMode = "throw";

    // Taken when the service starts, because a released subject has left the context when it is stopped.
    private TestHostedLog? _log;

    [Configuration]
    public partial string? Name { get; set; }

    /// <summary>With <see cref="ThrowMode"/> applying the configuration fails.</summary>
    [Configuration]
    public partial string? Mode { get; set; }

    /// <summary>A value outside the range fails in the setter, after the constructor ran.</summary>
    [Configuration]
    [Range(0, 10)]
    public partial int Level { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _log = ((IInterceptorSubject)this).Context.TryGetService<TestHostedLog>();
        _log?.Started(this);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log?.Stopped(this);
        return Task.CompletedTask;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) =>
        Mode == ThrowMode ? throw new InvalidOperationException("apply failed") : Task.CompletedTask;
}

/// <summary>
/// A subject whose own lifecycle handler fails when the subject leaves the context, which a handler must
/// not do. A value outside the range of <see cref="Level"/> fails in the setter, after the constructor ran.
/// </summary>
[InterceptorSubject]
public partial class TestFailingDetach : IConfigurable, ILifecycleHandler
{
    [Configuration]
    [Range(0, 10)]
    public partial int Level { get; set; }

    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
        if (change.IsContextDetach)
        {
            throw new InvalidOperationException("detach failed");
        }
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A subject with a property that has a unit, for the formatted text of an expression.</summary>
[InterceptorSubject]
public partial class TestMeter : IConfigurable
{
    [Configuration]
    [State(Unit = StateUnit.Watt)]
    public partial decimal Power { get; set; }

    [Configuration]
    [State]
    public partial bool IsOn { get; set; }

    [Configuration]
    [State(Unit = StateUnit.DegreeCelsius)]
    public partial decimal? Temperature { get; set; }

    [Configuration]
    [State("Flow rate")]
    public partial decimal Flow { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>A layout whose values are configuration in a file and slot blocks on a page.</summary>
[InterceptorSubject]
public partial class TestBoard : IConfigurable
{
    public TestBoard()
    {
        Tiles = [];
    }

    [Configuration]
    public partial int Columns { get; set; }

    [Configuration, Slot("Tile")]
    public partial TestTile[] Tiles { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>One value of a <see cref="TestBoard"/>. Its child is a subject in a file and content on a page.</summary>
[InterceptorSubject]
public partial class TestTile : IConfigurable
{
    [Configuration]
    public partial int Position { get; set; }

    [Configuration, Slot(IsDefault = true)]
    public partial IInterceptorSubject? Child { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Runs code of the test while its configuration is applied, which is inside the reload of its page.</summary>
[InterceptorSubject]
public partial class TestCallback : IConfigurable
{
    [Configuration]
    public partial string? Label { get; set; }

    /// <summary>Awaited while the configuration is applied. Null does nothing.</summary>
    public Func<Task>? OnApply { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) =>
        OnApply?.Invoke() ?? Task.CompletedTask;
}

/// <summary>A subject with a list of subjects in its configuration, which a page writes in the json block.</summary>
[InterceptorSubject]
public partial class TestShelf : IConfigurable
{
    public TestShelf()
    {
        Rows = 2;
        Items = [];
    }

    [Configuration]
    public partial int Rows { get; set; }

    [Configuration]
    public partial int Columns { get; set; }

    [Configuration]
    public partial List<TestShelfItem> Items { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>One entry of a <see cref="TestShelf"/> with a subject of any type.</summary>
[InterceptorSubject]
public partial class TestShelfItem : IConfigurable
{
    [Configuration]
    public partial int? Row { get; set; }

    [Configuration]
    public partial int? Column { get; set; }

    [Configuration]
    public partial int ColumnSpan { get; set; }

    [Configuration]
    public partial IInterceptorSubject? Child { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
