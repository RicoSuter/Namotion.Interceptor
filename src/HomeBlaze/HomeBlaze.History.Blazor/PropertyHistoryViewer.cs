using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Components;
using HomeBlaze.History;
using HomeBlaze.Services;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// Shows the history of a property in the <see cref="PropertyHistoryDialog"/>, when the viewer of the circuit may read it.
/// </summary>
internal sealed class PropertyHistoryViewer : IPropertyHistoryViewer
{
    private readonly IDialogService _dialogService;
    private readonly SubjectPathResolver _pathResolver;
    private readonly IInterceptorSubjectContext _rootContext;
    private readonly IUserPermissions _permissions;

    public PropertyHistoryViewer(IDialogService dialogService, SubjectPathResolver pathResolver, IInterceptorSubjectContext rootContext, IUserPermissions permissions)
    {
        _dialogService = dialogService;
        _pathResolver = pathResolver;
        _rootContext = rootContext;
        _permissions = permissions;
    }

    // Whether the viewer may read the history and the property is recorded under a path (GetHistoryPath without joining it),
    // which the dialog queries. Looking for a store would walk every known subject on every render of every widget, and
    // without a store the dialog shows an empty history.
    public bool CanShow(RegisteredSubjectProperty property) =>
        property.HasHistory() && _permissions.CanReadHistory(property) &&
        _pathResolver.GetPath(property.Subject, PathStyle.Canonical) is not null;

    public async Task ShowAsync(RegisteredSubjectProperty property, string displayName)
    {
        if (_rootContext.TryGetService<ISubjectRegistry>() is { } registry)
        {
            await PropertyHistoryDialog.ShowAsync(_dialogService, registry, _pathResolver, _permissions, property, displayName);
        }
    }
}
