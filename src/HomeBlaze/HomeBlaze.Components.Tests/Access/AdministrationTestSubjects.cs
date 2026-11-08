using System.Collections.Concurrent;
using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Storage.Abstractions;
using Microsoft.AspNetCore.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Parent;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>A device that the generated configuration editor edits.</summary>
[InterceptorSubject]
public partial class AdministeredDevice : IConfigurable
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration]
    public partial string? Label { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>A device that <see cref="HostedDeviceEditComponent"/> edits.</summary>
[InterceptorSubject]
public partial class HostedDevice : IConfigurable
{
    [Configuration]
    public partial string? Host { get; set; }

    [Configuration]
    public partial string? Label { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>An edit component that sets <see cref="HostedDevice.Host"/> to "new" when it saves.</summary>
[SubjectComponent(SubjectComponentType.Edit, typeof(HostedDevice))]
public sealed class HostedDeviceEditComponent : ComponentBase, ISubjectEditComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    public bool IsValid => true;

    public bool IsDirty => false;

    public event Action<bool>? IsValidChanged
    {
        add { }
        remove { }
    }

    public event Action<bool>? IsDirtyChanged
    {
        add { }
        remove { }
    }

    /// <summary>The number of saves.</summary>
    public int SaveCount { get; private set; }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        ((HostedDevice)Subject!).Host = "new";
        return Task.CompletedTask;
    }
}

/// <summary>A device that <see cref="SelfPersistedDeviceEditComponent"/> edits and stores without the host.</summary>
[InterceptorSubject]
public partial class SelfPersistedDevice : IConfigurable
{
    [Configuration]
    public partial string? Label { get; set; }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// An edit component that persists itself: its save puts a new device in the place of the edited one, as a store that
/// projects what it committed does.
/// </summary>
[SubjectComponent(SubjectComponentType.Edit, typeof(SelfPersistedDevice))]
public sealed class SelfPersistedDeviceEditComponent : ComponentBase, ISubjectEditComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    public bool IsValid => true;

    public bool IsDirty => false;

    public bool PersistsItself => true;

    public event Action<bool>? IsValidChanged
    {
        add { }
        remove { }
    }

    public event Action<bool>? IsDirtyChanged
    {
        add { }
        remove { }
    }

    /// <summary>The number of saves.</summary>
    public int SaveCount { get; private set; }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        var store = (AdministeredStore)Subject!.GetParents().Single().Property.Subject;
        store.SelfPersisted = new SelfPersistedDevice { Label = "new" };
        return Task.CompletedTask;
    }
}

/// <summary>A device without configuration members whose edit component saves through its edit operation.</summary>
[InterceptorSubject]
public partial class OperationSavedDevice
{
    [State]
    public partial string? Label { get; set; }

    [Operation(Title = "Save", RequiresAdministration = true, Purpose = OperationPurpose.Edit)]
    public void Save(string label) => Label = label;
}

/// <summary>An edit component that saves "new" as the label through the edit operation of the device.</summary>
[SubjectComponent(SubjectComponentType.Edit, typeof(OperationSavedDevice))]
public sealed class OperationSavedDeviceEditComponent : ComponentBase, ISubjectEditComponent
{
    [Inject]
    public IUserPermissions Permissions { get; set; } = null!;

    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    public bool IsValid => true;

    public bool IsDirty => false;

    public bool PersistsItself => true;

    public event Action<bool>? IsValidChanged
    {
        add { }
        remove { }
    }

    public event Action<bool>? IsDirtyChanged
    {
        add { }
        remove { }
    }

    /// <summary>The number of saves.</summary>
    public int SaveCount { get; private set; }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        var result = await Permissions.TryInvokeAsync(Subject!, nameof(OperationSavedDevice.Save), ["new"], null, cancellationToken);
        if (result.Outcome != PermittedOperationOutcome.Completed)
        {
            throw new InvalidOperationException("The device was not saved.");
        }
    }
}

/// <summary>A device without configuration members and without an edit operation, which has an edit component.</summary>
[InterceptorSubject]
public partial class UnconfiguredDevice
{
    [State]
    public partial string? Label { get; set; }
}

/// <summary>An edit component that sets <see cref="UnconfiguredDevice.Label"/> to "new" when it saves.</summary>
[SubjectComponent(SubjectComponentType.Edit, typeof(UnconfiguredDevice))]
public sealed class UnconfiguredDeviceEditComponent : ComponentBase, ISubjectEditComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    public bool IsValid => true;

    public bool IsDirty => false;

    public event Action<bool>? IsValidChanged
    {
        add { }
        remove { }
    }

    public event Action<bool>? IsDirtyChanged
    {
        add { }
        remove { }
    }

    /// <summary>The number of saves.</summary>
    public int SaveCount { get; private set; }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        ((UnconfiguredDevice)Subject!).Label = "new";
        return Task.CompletedTask;
    }
}

/// <summary>A configuration writer that holds the devices and records what it was asked to write.</summary>
[InterceptorSubject]
public partial class AdministeredStore : IConfigurationWriter
{
    private readonly ConcurrentQueue<IInterceptorSubject> _writtenSubjects = new();

    public partial AdministeredDevice? Device { get; set; }

    public partial HostedDevice? Hosted { get; set; }

    public partial SelfPersistedDevice? SelfPersisted { get; set; }

    public partial OperationSavedDevice? OperationSaved { get; set; }

    public partial UnconfiguredDevice? Unconfigured { get; set; }

    public IReadOnlyCollection<IInterceptorSubject> WrittenSubjects => _writtenSubjects;

    /// <summary>The operation whose member gates creating a subject in the store.</summary>
    [Operation(Title = "Create")]
    public Task CreateAsync() => Task.CompletedTask;

    public Task<bool> WriteConfigurationAsync(IInterceptorSubject subject, CancellationToken cancellationToken)
    {
        _writtenSubjects.Enqueue(subject);
        return Task.FromResult(true);
    }
}
