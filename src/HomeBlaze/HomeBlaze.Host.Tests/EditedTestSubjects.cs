using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions;
using HomeBlaze.Components.Abstractions.Attributes;
using Microsoft.AspNetCore.Components;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Attributes;

namespace HomeBlaze.Host.Tests;

/// <summary>A subject without configuration members and without an edit operation, which has an edit component.</summary>
[InterceptorSubject]
public partial class UnconfiguredEditedSubject
{
    [State]
    public partial string? Label { get; set; }
}

/// <summary>A subject without configuration members whose edit component saves through its edit operation.</summary>
[InterceptorSubject]
public partial class OperationSavedSubject
{
    [State]
    public partial string? Label { get; set; }

    public partial bool IsLocked { get; set; }

    [Derived]
    [PropertyAttribute("Save", KnownAttributes.IsEnabled)]
    public bool IsSaveEnabled => !IsLocked;

    [Operation(Title = "Save", RequiresAdministration = true, Purpose = OperationPurpose.Edit)]
    public void Save(string label) => Label = label;
}

/// <summary>The edit component of <see cref="UnconfiguredEditedSubject"/> and <see cref="OperationSavedSubject"/>.</summary>
[SubjectComponent(SubjectComponentType.Edit, typeof(UnconfiguredEditedSubject))]
[SubjectComponent(SubjectComponentType.Edit, typeof(OperationSavedSubject))]
public sealed class EditedTestSubjectEditComponent : ComponentBase, ISubjectEditComponent
{
    [Parameter]
    public IInterceptorSubject? Subject { get; set; }

    [Parameter]
    public bool IsCreating { get; set; }

    public bool IsValid => true;

    public bool IsDirty => false;

    // Never raised: the validity and the dirty state never change.
    public event Action<bool>? IsValidChanged
    {
        add
        {
            // Nothing to subscribe to.
        }

        remove
        {
            // Nothing was subscribed.
        }
    }

    public event Action<bool>? IsDirtyChanged
    {
        add
        {
            // Nothing to subscribe to.
        }

        remove
        {
            // Nothing was subscribed.
        }
    }

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
