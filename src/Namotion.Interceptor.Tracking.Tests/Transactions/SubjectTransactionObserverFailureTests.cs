using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;
using Namotion.Interceptor.Tracking.Transactions;
using static Namotion.Interceptor.Tracking.Tests.Transactions.TransactionTestContext;

namespace Namotion.Interceptor.Tracking.Tests.Transactions;

[Collection(PerPropertySubscriptionCollection.Name)]
public class SubjectTransactionObserverFailureTests
{
    public SubjectTransactionObserverFailureTests() => PropertyChangeSubscriptions.ResetForTests();

    [Theory]
    [InlineData(TransactionFailureHandling.Rollback, "X", "Y")]
    [InlineData(TransactionFailureHandling.BestEffort, "X", "B")]
    public async Task WhenObserverThrowsAfterApplyStoredTheValue_ThenTheFailedChangeIsRestored(
        TransactionFailureHandling failureHandling, string expectedFirstName, string expectedLastName)
    {
        // Arrange
        var context = CreateContext();
        var person = new Person(context) { FirstName = "X", LastName = "Y" };
        using var subscription = new PropertyReference(person, nameof(Person.FirstName))
            .SubscribeInline((in SubjectPropertyChange _) => throw new InvalidOperationException("Observer failed."));

        // Act
        SubjectTransactionException exception;
        using (var transaction = await context.BeginTransactionAsync(failureHandling))
        {
            person.FirstName = "A";
            person.LastName = "B";
            exception = await Assert.ThrowsAsync<SubjectTransactionException>(
                () => transaction.CommitAsync(CancellationToken.None).AsTask());
        }

        // Assert
        Assert.Equal(expectedFirstName, person.FirstName);
        Assert.Equal(expectedLastName, person.LastName);
        // The restore stored the old value before the observer threw again, so FirstName is not reported as stuck.
        Assert.Single(exception.FailedChanges, change => change.Property.Name == nameof(Person.FirstName));
        Assert.DoesNotContain(exception.AppliedChanges, change => change.Property.Name == nameof(Person.FirstName));
        Assert.All(exception.Errors, error => Assert.Equal("Observer failed.", error.Message));
    }
}
