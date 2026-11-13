using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class DerivedPropertyLinkOwnershipTests
{
    [Fact]
    public void WhenSourceReachedThroughFieldIsDetached_ThenDerivedPropertyKeepsFollowingIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var source = new Person { FirstName = "a" };
        var root = new Person(context) { Mother = source };
        var reader = new FieldReadingSubject { Source = source };
        _ = new ReaderRoot(context) { Reader = reader };
        root.Mother = null;
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        source.FirstName = "b";

        // Assert
        Assert.Single(changes, c => c.Property.Subject == reader && c.Property.Name == nameof(FieldReadingSubject.SourceName));
    }

    [Fact]
    public void WhenDerivedPropertyIsDetached_ThenSourceReachedThroughFieldNoLongerReferencesIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var source = new Person { FirstName = "a" };
        var readerRoot = new ReaderRoot(context);
        var weakReader = AttachAndDetachReader(readerRoot, source);

        // Act
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Assert
        Assert.False(weakReader.IsAlive);
        Assert.Empty(new PropertyReference(source, nameof(Person.FirstName)).GetUsedByProperties().Items.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AttachAndDetachReader(ReaderRoot root, Person source)
    {
        var reader = new FieldReadingSubject { Source = source };
        root.Reader = reader;
        root.Reader = null;
        return new WeakReference(reader);
    }
}
