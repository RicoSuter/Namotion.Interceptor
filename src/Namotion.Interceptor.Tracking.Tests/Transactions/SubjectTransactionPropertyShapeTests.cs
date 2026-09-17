using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

public class SubjectTransactionPropertyShapeTests
{
    [Fact]
    public async Task WhenPropertyIsAddedAtRuntime_ThenTransactionCommitsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry().WithTransactions();
        var subject = new ShapeSubject(context);
        var stored = "old";
        subject.TryGetRegisteredSubject()!.AddProperty(
            "Runtime", typeof(string), _ => stored, (_, value) => stored = (string?)value ?? "");
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        subject.TryGetRegisteredSubject()!.TryGetProperty("Runtime")!.SetValue("new");

        // The write must be deferred, otherwise the assertions below would also hold
        // for a pipeline that never captured the property into the transaction at all.
        Assert.Equal("old", stored);
        Assert.Single(transaction.GetPendingChanges(), change => change.Property.Name == "Runtime");

        // Act
        await transaction.CommitAsync(default);

        // Assert
        Assert.Equal("new", stored);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenPropertyIsVirtual_ThenTransactionCommitsIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var subject = new ShapeSubject(context) { VirtualValue = "old" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        subject.VirtualValue = "new";

        // Reading the property inside the transaction serves the pending value either way,
        // so the captured change is what proves the write was deferred rather than applied.
        Assert.Single(transaction.GetPendingChanges(), change => change.Property.Name == nameof(ShapeSubject.VirtualValue));

        // Act
        await transaction.CommitAsync(default);

        // Assert
        Assert.Equal("new", subject.VirtualValue);
        Assert.Empty(transaction.GetPendingChanges());
    }

    [Fact]
    public async Task WhenPropertyIsOverridden_ThenTransactionCommitsTheDerivedValue()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithTransactions();
        var subject = new DerivedShapeSubject(context) { VirtualValue = "old" };
        using var transaction = await context.BeginTransactionAsync(TransactionFailureHandling.Rollback);
        subject.VirtualValue = "new";

        // Reading the property inside the transaction serves the pending value either way,
        // so the captured change is what proves the write was deferred rather than applied.
        Assert.Single(transaction.GetPendingChanges(), change => change.Property.Name == nameof(DerivedShapeSubject.VirtualValue));

        // Act
        await transaction.CommitAsync(default);

        // Assert
        Assert.Equal("new", subject.VirtualValue);
        Assert.Empty(transaction.GetPendingChanges());
    }
}

[InterceptorSubject]
public partial class ShapeSubject
{
    public virtual partial string? VirtualValue { get; set; }
}

[InterceptorSubject]
public partial class DerivedShapeSubject : ShapeSubject
{
    public override partial string? VirtualValue { get; set; }
}
