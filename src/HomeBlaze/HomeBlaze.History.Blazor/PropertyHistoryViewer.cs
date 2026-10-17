using HomeBlaze.Components;
using HomeBlaze.History;
using HomeBlaze.Services;
using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.History.Blazor;

/// <summary>
/// Shows the history of a property in the <see cref="PropertyHistoryDialog"/>.
/// </summary>
internal sealed class PropertyHistoryViewer : IPropertyHistoryViewer
{
    private readonly IDialogService _dialogService;
    private readonly SubjectPathResolver _pathResolver;
    private readonly IInterceptorSubjectContext _rootContext;

    public PropertyHistoryViewer(IDialogService dialogService, SubjectPathResolver pathResolver, IInterceptorSubjectContext rootContext)
    {
        _dialogService = dialogService;
        _pathResolver = pathResolver;
        _rootContext = rootContext;
    }

    // Whether the property is recorded under a path, which the dialog queries. Looking for a store would walk every known
    // subject on every render of every widget, and without a store the dialog shows an empty history.
    public bool CanShow(RegisteredSubjectProperty property) =>
        property.HasHistory() && property.GetHistoryPath(_pathResolver) is not null;

    public async Task ShowAsync(RegisteredSubjectProperty property, string displayName)
    {
        if (_rootContext.TryGetService<ISubjectRegistry>() is { } registry)
        {
            await PropertyHistoryDialog.ShowAsync(_dialogService, registry, _pathResolver, property, displayName);
        }
    }
}
