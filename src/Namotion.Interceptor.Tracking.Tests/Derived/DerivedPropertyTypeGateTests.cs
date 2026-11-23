using System.Reactive.Concurrency;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Derived;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Derived;

public class DerivedPropertyTypeGateTests
{
    [Fact]
    public void WhenNoPropertyOfTypeIsTracked_ThenGateStaysDownAndWriteCreatesNoData()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var withContext = new UntrackedGateSubject(context);
        var withoutContext = new UntrackedGateSubject();

        // Act
        withContext.Value = "a";
        withoutContext.Value = "a";

        // Assert
        Assert.False(DerivedPropertyTypeGate.Of(withContext).IsRaised);
        Assert.Null(new PropertyReference(withContext, nameof(UntrackedGateSubject.Value)).TryGetDerivedPropertyData());
        Assert.Null(new PropertyReference(withoutContext, nameof(UntrackedGateSubject.Value)).TryGetDerivedPropertyData());
    }

    [Fact]
    public void WhenGateOfCollectibleTypeWasLookedUp_ThenTypeCanBeCollected()
    {
        // Arrange
        var type = LookUpGateOfCollectibleType();

        // Act: the thread's last gate moves to another type, so only the gate table could still keep the type.
        DerivedPropertyTypeGate.Of(typeof(UntrackedGateSubject));
        for (var i = 0; i < 10 && type.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // Assert
        Assert.False(type.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LookUpGateOfCollectibleType()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("CollectibleGateAssembly"), AssemblyBuilderAccess.RunAndCollect);
        var type = assembly.DefineDynamicModule("CollectibleGateModule").DefineType("CollectibleGateType").CreateType();
        DerivedPropertyTypeGate.Of(type);
        return new WeakReference(type);
    }

    [Fact]
    public void WhenPropertyOfTypeIsReadByDerivedProperty_ThenGateIsRaisedAndLaterWritesArePublished()
    {
        // Arrange: the gate stays raised for the process, so a rerun in the same process finds it raised already.
        // The order of the raise against a concurrent write is covered by the proof in
        // docs/design/tracking-derived-properties.md, not by a test.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var source = new TrackedGateSource { Value = "a" };
        var reader = new TrackedGateReader { Source = source };
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        var root = new TrackedGateRoot(context) { Reader = reader };
        source.Value = "b";

        // Assert
        Assert.True(DerivedPropertyTypeGate.Of(source).IsRaised);
        var change = Assert.Single(changes, c => c.Property.Name == nameof(TrackedGateReader.SourceValue));
        Assert.Equal("b", change.GetNewValue<string?>());
        Assert.Same(reader, root.Reader);
    }
}
