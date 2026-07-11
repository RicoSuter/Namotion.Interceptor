using System.ComponentModel;
using Namotion.Interceptor.Generator.Tests.Models;
using Xunit;

namespace Namotion.Interceptor.Generator.Tests;

public class HookScopeGenerationTests
{
    [Fact]
    public void WhenHookImplemented_ThenHookBodyRunsUnderLocalOriginScope()
    {
        // Arrange
        var person = new PersonWithSelectiveHooks();
        var source = new object();

        // Act: write under an ambient source scope, like an inbound/commit apply.
        using (SubjectChangeContext.WithSource(source))
        {
            person.Hooked = "value";
        }

        // Assert: the implemented hook saw a null source (the generated scope reset it),
        // even though the surrounding write ran under a real source scope.
        Assert.Null(person.HookedSourceInsideChanged);
    }

    [Fact]
    public void WhenHookNotImplemented_ThenSetterStillWritesValue()
    {
        // Arrange
        var person = new PersonWithSelectiveHooks();

        // Act: a property whose hooks are not implemented pays nothing and behaves normally.
        person.NotHooked = "value";

        // Assert
        Assert.Equal("value", person.NotHooked);
    }

    [Fact]
    public void WhenBaseManuallyImplementsRaisePropertyChanged_ThenRaiseRunsUnderLocalOriginScope()
    {
        // The manual base's raiser owns the local-origin contract.
        var subject = new ManualInpcSubject();
        AssertHandlerSeesNullSourceWhenWrittenUnderSourceScope(subject, () => subject.Name = "value");
    }

    [Fact]
    public void WhenMultiLevelChainRootedAtManualBase_ThenRaiseRunsUnderLocalOriginScope()
    {
        // ManualInpcGrandchild : ManualInpcSubject : ManualInpcBase; the raise is inherited.
        var subject = new ManualInpcGrandchild();
        AssertHandlerSeesNullSourceWhenWrittenUnderSourceScope(subject, () => subject.GrandchildName = "value");
    }

    [Fact]
    public void WhenBaseSubjectManuallyImplementsInpcItself_ThenChildRaiseRunsUnderLocalOriginScope()
    {
        // SelfInpcChild : SelfInpcSubject, where the parent is a generated subject that declares
        // IRaisePropertyChanged manually in its own base list and owns the raise contract.
        var subject = new SelfInpcChild();
        AssertHandlerSeesNullSourceWhenWrittenUnderSourceScope(subject, () => subject.ChildName = "value");
    }

    [Fact]
    public void WhenLocalWriteRaisesPropertyChanged_ThenHandlerSeesNullSource()
    {
        // Arrange: the generated RaisePropertyChanged skips the local-origin scope when the ambient
        // source is already null; the handler must still observe a null source.
        var person = new PersonWithSelectiveHooks();
        object? sourceSeenDuringRaise = "sentinel";
        person.PropertyChanged += (_, _) => sourceSeenDuringRaise = SubjectChangeContext.Current.Source;

        // Act
        person.NotHooked = "value";

        // Assert
        Assert.Null(sourceSeenDuringRaise);
    }

    [Fact]
    public void WhenBaseImplementsRaiseExplicitlyOnly_ThenForwarderDeliversRaiseUnderLocalOriginScope()
    {
        // ExplicitInpcSubject : ExplicitInpcBase, where the base has no callable raiser, so the
        // generated setter raises through the emitted protected forwarder.
        var subject = new ExplicitInpcSubject();
        AssertHandlerSeesNullSourceWhenWrittenUnderSourceScope(subject, () => subject.Name = "value");
    }

    [Fact]
    public void WhenBaseRaiserIsPrivateProtected_ThenChildCallsItDirectlyUnderLocalOriginScope()
    {
        // The same-assembly private protected raiser is callable from the generated child, so no
        // forwarder is emitted (one would hide the base method and fail the build).
        var subject = new PrivateProtectedRaiseSubject();
        AssertHandlerSeesNullSourceWhenWrittenUnderSourceScope(subject, () => subject.Name = "value");
    }

    /// <summary>
    /// Writes through <paramref name="write"/> under an ambient source scope and asserts the
    /// subject's PropertyChanged handler fired and observed a null source (the raise entered a
    /// local-origin scope).
    /// </summary>
    private static void AssertHandlerSeesNullSourceWhenWrittenUnderSourceScope(
        INotifyPropertyChanged subject, Action write)
    {
        // Arrange: seeded non-null so "handler saw null" is distinguishable from "handler never ran".
        object? sourceSeenDuringRaise = "sentinel";
        subject.PropertyChanged += (_, _) => sourceSeenDuringRaise = SubjectChangeContext.Current.Source;

        // Act
        using (SubjectChangeContext.WithSource(new object()))
        {
            write();
        }

        // Assert
        Assert.Null(sourceSeenDuringRaise);
    }
}
