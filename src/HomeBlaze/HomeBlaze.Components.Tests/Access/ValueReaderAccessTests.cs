using HomeBlaze.Abstractions.Devices.Locks;
using HomeBlaze.Services;
using HomeBlaze.Services.Display;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry.Abstractions;
using Xunit;

namespace HomeBlaze.Components.Tests.Access;

public class ValueReaderAccessTests
{
    private static readonly string TextPath = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Text));
    private static readonly string LockPath = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Lock));

    private readonly AccessTestDevice _device;
    private readonly SubjectPathResolver _resolver;
    private readonly TestUserPermissions _permissions = new();

    public ValueReaderAccessTests()
    {
        var context = SubjectContextFactory.Create(new ServiceCollection());
        var device = _device = new AccessTestDevice(context)
        {
            Text = "Value",
            Lock = new AccessTestLock { LockState = DoorLockState.Locked },
            Devices = [new AccessTestRow { Name = "Row" }]
        };
        _resolver = new SubjectPathResolver(() => device);
    }

    [Fact]
    public void WhenAPropertyIsHidden_ThenItResolvesAndReadsAsAPathThatLeadsNowhere()
    {
        // Arrange
        Hide(_device, nameof(AccessTestDevice.Text));

        // Act
        var property = ValueReader.ResolveProperty(_resolver, _device, TextPath, _permissions);
        var value = ValueReader.ReadValue(_resolver, _device, TextPath, _permissions);
        var hidden = ValueReader.Read(_resolver, _device, TextPath, null, null, _permissions);
        var missing = ValueReader.Read(_resolver, _device, WidgetTestGraph.MissingPath, null, null, _permissions);

        // Assert
        Assert.Null(property);
        Assert.Null(value);
        Assert.Equal(missing, hidden);
        Assert.True(hidden.IsMissing);
    }

    [Fact]
    public void WhenAResolvedPropertyIsHidden_ThenReadResolvedGivesTheReadingOfNoProperty()
    {
        // Arrange
        var property = Hide(_device, nameof(AccessTestDevice.Text));

        // Act
        var hidden = ValueReader.ReadResolved(_resolver, property, null, null, _permissions);
        var none = ValueReader.ReadResolved(_resolver, null, null, null, _permissions);

        // Assert
        Assert.Equal(none, hidden);
    }

    [Fact]
    public void WhenAPropertyOfASubjectIsHidden_ThenReadPropertyGivesTheReadingOfAPropertyTheSubjectDoesNotHave()
    {
        // Arrange
        Hide(_device, nameof(AccessTestDevice.Text));

        // Act
        var hidden = ValueReader.ReadProperty(_resolver, _device, nameof(AccessTestDevice.Text), null, null, _permissions);
        var unknown = ValueReader.ReadProperty(_resolver, _device, "Unknown", null, null, _permissions);

        // Assert
        Assert.Equal(unknown, hidden);
    }

    [Fact]
    public void WhenAReferenceIsHidden_ThenResolveSubjectDoesNotFollowIt()
    {
        // Arrange
        Hide(_device, nameof(AccessTestDevice.Lock));

        // Act
        var subject = ValueReader.ResolveSubject(_resolver, _device, LockPath, _permissions);

        // Assert
        Assert.Null(subject);
    }

    [Fact]
    public void WhenAReferenceIsReadable_ThenResolveSubjectGivesItsSubject()
    {
        // Act
        var subject = ValueReader.ResolveSubject(_resolver, _device, LockPath, _permissions);

        // Assert
        Assert.Same(_device.Lock, subject);
    }

    [Fact]
    public void WhenTheSubjectOfAReadablePropertyIsNotShown_ThenItResolvesAndReadsAsAPathThatLeadsNowhere()
    {
        // Arrange
        var path = LockPath + "/" + nameof(AccessTestLock.LockState);
        _permissions.CannotShow.Add(_device.Lock!);

        // Act
        var property = ValueReader.ResolveProperty(_resolver, _device, path, _permissions);
        var value = ValueReader.ReadValue(_resolver, _device, path, _permissions);
        var hidden = ValueReader.Read(_resolver, _device, path, null, null, _permissions);
        var missing = ValueReader.Read(_resolver, _device, WidgetTestGraph.MissingPath, null, null, _permissions);

        // Assert
        Assert.Null(property);
        Assert.Null(value);
        Assert.Equal(missing, hidden);
    }

    [Fact]
    public void WhenAPathEndsAtASubjectThatIsNotShown_ThenItBindsNothing()
    {
        // Arrange
        var path = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Devices)) + "[0]";
        var shown = ValueBinding.FromPath(_resolver, _device, path, _permissions);
        _permissions.CannotShow.Add(_device.Devices[0]);

        // Act
        var hidden = ValueBinding.FromPath(_resolver, _device, path, _permissions);

        // Assert
        Assert.Same(_device.Devices[0], shown.Subject);
        Assert.True(hidden.IsEmpty);
    }

    [Fact]
    public void WhenAReferenceOnThePathIsNotReadable_ThenAPropertyBehindItResolvesAndReadsAsAPathThatLeadsNowhere()
    {
        // Arrange
        var path = LockPath + "/" + nameof(AccessTestLock.LockState);
        _device.Widget = _device.Lock;
        Hide(_device, nameof(AccessTestDevice.Lock));

        // Act
        var property = ValueReader.ResolveProperty(_resolver, _device, path, _permissions);
        var value = ValueReader.ReadValue(_resolver, _device, path, _permissions);
        var hidden = ValueReader.Read(_resolver, _device, path, null, null, _permissions);
        var relative = ValueReader.ReadValue(_resolver, _device, path[1..], _permissions);
        var missing = ValueReader.Read(_resolver, _device, WidgetTestGraph.MissingPath, null, null, _permissions);

        // Assert
        Assert.Null(property);
        Assert.Null(value);
        Assert.Null(relative);
        Assert.Equal(missing, hidden);
    }

    [Fact]
    public void WhenACollectionOnThePathIsNotReadable_ThenTheSubjectInItBindsNothing()
    {
        // Arrange
        var path = WidgetTestGraph.PathOf(nameof(AccessTestDevice.Devices)) + "[0]";
        _device.Widget = _device.Devices[0];
        var shown = ValueBinding.FromPath(_resolver, _device, path, _permissions);
        Hide(_device, nameof(AccessTestDevice.Devices));

        // Act
        var hidden = ValueBinding.FromPath(_resolver, _device, path, _permissions);
        var subject = ValueReader.ResolveSubject(_resolver, _device, path, _permissions);
        var name = ValueReader.ReadValue(_resolver, _device, path + "/" + nameof(AccessTestRow.Name), _permissions);

        // Assert
        Assert.Same(_device.Devices[0], shown.Subject);
        Assert.True(hidden.IsEmpty);
        Assert.Null(subject);
        Assert.Null(name);
    }

    [Fact]
    public void WhenAReferenceOnThePathOfATextIsNotReadable_ThenTheTextDoesNotShowTheValueBehindIt()
    {
        // Arrange
        _device.Widget = _device.Lock;
        var text = new WidgetText("Label", ExpressionUse.WidgetText);
        var template = "[{{ /Lock/LockState }}] [{{ subject/Lock/LockState }}]";
        var binding = new ValueBinding(null, null, _device);
        var shown = text.Render(template, _resolver, _device, binding, null, _permissions, out _);
        Hide(_device, nameof(AccessTestDevice.Lock));

        // Act
        var hidden = text.Render(template, _resolver, _device, binding, null, _permissions, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal("[Locked] [Locked]", shown);
        Assert.Equal("[] []", hidden);
    }

    private RegisteredSubjectProperty Hide(IInterceptorSubject subject, string name)
    {
        var property = WidgetTestGraph.GetProperty(subject, name);
        _permissions.CannotRead.Add(property);
        return property;
    }
}
