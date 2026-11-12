using Bunit;
using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Abstractions.Sensors;
using HomeBlaze.Components.Abstractions.Attributes;
using HomeBlaze.Components.Abstractions.TimeZones;
using HomeBlaze.Components.Controls;
using HomeBlaze.Services;
using HomeBlaze.Services.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Components.Tests.Access;

/// <summary>
/// A graph whose root is an <see cref="AccessTestDevice"/>, with the services the widgets of this assembly need registered
/// on a bUnit context. The viewer is registered by the test.
/// </summary>
public sealed class WidgetTestGraph : IAsyncDisposable
{
    /// <summary>A path that leads nowhere.</summary>
    public const string MissingPath = "/Nothing";

    private readonly BunitContext _bunit;
    private readonly ServiceProvider _subjectServices;

    private WidgetTestGraph(BunitContext bunit, string text)
    {
        _bunit = bunit;
        bunit.JSInterop.Mode = JSRuntimeMode.Loose;
        bunit.Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);

        Context = SubjectContextFactory.Create(new ServiceCollection());
        var typeProvider = new TypeProvider().AddAssembly(typeof(Widget).Assembly);
        RootManager? rootManager = null;
        PathResolver = new SubjectPathResolver(() => rootManager?.Root);

        var subjectServices = new ServiceCollection();
        subjectServices.AddSingleton(Context);
        subjectServices.AddSingleton(PathResolver);
        subjectServices.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        _subjectServices = subjectServices.BuildServiceProvider();

        RootManager = rootManager = new RootManager(
            new SubjectTypeRegistry(typeProvider),
            new ConfigurableSubjectSerializer(typeProvider, _subjectServices),
            Context,
            PathResolver);
        Device = new AccessTestDevice(Context)
        {
            Text = text,
            Flag = true,
            Mode = AccessTestMode.Eco,
            Level = 0.4,
            Lock = new AccessTestLock { LockState = DoorLockState.Locked, DoorState = DoorState.Open },
            Devices = [new AccessTestRow { Name = "Pump", Secret = "Pump secret" }, new AccessTestRow { Name = "Fan", Secret = "Fan secret" }]
        };
        RootManager.Root = Device;

        bunit.Services.AddSingleton(RootManager);
        bunit.Services.AddSingleton(PathResolver);
        bunit.Services.AddSingleton(new SubjectComponentRegistry(typeProvider));
        bunit.Services.AddSingleton(new SubjectFactory(_subjectServices));
        bunit.Services.AddSingleton<ITimeZoneDisplay, TimeZoneDisplayService>();
        bunit.Services.AddSingleton(TimeProvider.System);
        bunit.Services.AddScoped<PropertyWriter>();
    }

    public IInterceptorSubjectContext Context { get; }

    public SubjectPathResolver PathResolver { get; }

    public RootManager RootManager { get; }

    public AccessTestDevice Device { get; }

    /// <summary>Gets the absolute path of a member of the root.</summary>
    public static string PathOf(string memberName) => "/" + memberName;

    public static WidgetTestGraph Create(BunitContext bunit, string text) => new(bunit, text);

    /// <summary>Gets a member of the root.</summary>
    public RegisteredSubjectProperty GetProperty(string name) => GetProperty(Device, name);

    /// <summary>Gets a member of a subject of the graph.</summary>
    public static RegisteredSubjectProperty GetProperty(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    /// <summary>
    /// Creates a widget of the type, sets every string property whose name ends in "Path", and the source of a table, to
    /// the path and puts it into the graph. A <see cref="ValueRow"/> only shows a value inside a list, so it is put into a
    /// <see cref="ValueList"/> and the list is returned. A table gets a column of <see cref="AccessTestRow.Name"/>.
    /// </summary>
    public IInterceptorSubject CreateWidget(Type widgetType, string path)
    {
        var widget = CreateSubject(widgetType);
        foreach (var property in widgetType.GetProperties())
        {
            if (property.PropertyType == typeof(string) && property.CanWrite && IsPathArgument(property.Name))
            {
                property.SetValue(widget, path);
            }
        }

        if (widget is SubjectTable table)
        {
            var column = CreateSubject<SubjectTableColumn>();
            column.Property = nameof(AccessTestRow.Name);
            table.Columns = [column];
        }

        if (widget is ValueRow row)
        {
            var list = CreateSubject<ValueList>();
            list.Values = [row];
            widget = list;
        }

        Device.Widget = widget;
        return widget;
    }

    /// <summary>Whether a string argument of a widget is a path to what it shows.</summary>
    public static bool IsPathArgument(string propertyName) =>
        propertyName.EndsWith("Path", StringComparison.Ordinal) || propertyName == nameof(SubjectTable.Source);

    /// <summary>Creates a subject as the application does, with its services.</summary>
    public T CreateSubject<T>() where T : IInterceptorSubject => (T)CreateSubject(typeof(T));

    /// <summary>Renders the widget component of the subject as a page does.</summary>
    public IRenderedComponent<SubjectComponent> RenderWidget(IInterceptorSubject widget) =>
        _bunit.Render<SubjectComponent>(parameters => parameters
            .Add(component => component.Subject, widget)
            .Add(component => component.Type, SubjectComponentType.Widget));

    public async ValueTask DisposeAsync()
    {
        RootManager.Dispose();
        await _subjectServices.DisposeAsync();
    }

    private IInterceptorSubject CreateSubject(Type type) => new SubjectFactory(_subjectServices).CreateSubject(type);

}
