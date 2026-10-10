using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

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
    [Slot]
    public partial string? Text { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public class TwoDefaults
{
    [Slot(IsDefault = true)]
    public IInterceptorSubject? First { get; set; }

    [Slot(IsDefault = true)]
    public IInterceptorSubject? Second { get; set; }
}

public class TypedDefault
{
    [Slot(IsDefault = true)]
    public TestRow? Content { get; set; }
}

public class DottedName
{
    [Slot("A.B")]
    public IInterceptorSubject? Content { get; set; }
}

public class DuplicateNames
{
    [Slot("Item")]
    public IInterceptorSubject? First { get; set; }

    [Slot("Item")]
    public IInterceptorSubject? Second { get; set; }
}

public class EmptyName
{
    [Slot("")]
    public IInterceptorSubject? Content { get; set; }
}

public class WhitespaceName
{
    [Slot(" ")]
    public IInterceptorSubject? Content { get; set; }
}

public class StringSlot
{
    [Slot]
    public string? Content { get; set; }
}

public class ArrayDefault
{
    [Slot(IsDefault = true)]
    public IInterceptorSubject[] Content { get; set; } = [];
}

public interface ITestSlotValue : IInterceptorSubject;

public class InterfaceSlot
{
    [Slot]
    public ITestSlotValue? Content { get; set; }
}

public abstract class TestAbstractRow : TestRow;

public class AbstractSlot
{
    [Slot("Row")]
    public TestAbstractRow[] Rows { get; set; } = [];
}

public class TestSlotBase
{
    [Slot(IsDefault = true)]
    public virtual IInterceptorSubject? Content { get; set; }
}

public class TestSlotDerived : TestSlotBase
{
    public override IInterceptorSubject? Content { get; set; }
}
