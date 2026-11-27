using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// A subject is resolvable through the registry while it attaches, before its derived properties depend on their
/// sources, so a reader can read a derived value then and rely on its changes afterwards.
/// </summary>
public class DerivedPropertyAttachRegistryTests
{
    [Fact]
    public void WhenADerivedValueIsReadThroughTheRegistryWhileItsSubjectAttaches_ThenASourceWrittenBeforeItIsAttachedPublishesTheDerivedChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        var parent = new Person(context);
        var child = new Person();
        object? readWhileAttaching = null;
        context.AddService<ILifecycleHandler>(new RegisteredHook(child, () =>
        {
            var registeredChild = parent.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Father))!.Children.Single().Subject;
            readWhileAttaching = context.GetService<ISubjectRegistry>().TryGetRegisteredSubject(registeredChild)!
                .TryGetProperty(nameof(Person.FullName))!.GetValue();
            child.FirstName = "Ann";
        }));

        // Act
        parent.Father = child;

        // Assert
        Assert.Equal("NA", readWhileAttaching);
        var fullNameChange = Assert.Single(changes, change => change.Property.Name == nameof(Person.FullName));
        Assert.Equal("NA", fullNameChange.GetOldValue<string>());
        Assert.Equal("Ann", fullNameChange.GetNewValue<string>());
    }

    /// <summary>
    /// Runs an action once when the subject attaches, after the registry registered it and before it inherits the
    /// context, so before its writes are published.
    /// </summary>
    [RunsAfter(typeof(SubjectRegistry))]
    [RunsBefore(typeof(ContextInheritanceHandler))]
    private sealed class RegisteredHook(IInterceptorSubject subject, Action onAttach) : ILifecycleHandler
    {
        private bool _hasRun;

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (change.IsContextAttach && ReferenceEquals(change.Subject, subject) && !_hasRun)
            {
                _hasRun = true;
                onAttach();
            }
        }
    }
}
