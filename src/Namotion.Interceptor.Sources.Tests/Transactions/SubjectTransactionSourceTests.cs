using Moq;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Sources.Tests.Models;
using Namotion.Interceptor.Sources.Transactions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Sources.Tests.Transactions;

/// <summary>
/// Tests for source integration: SetSource, TryGetSource, WriteChangesAsync, and multi-context scenarios.
/// </summary>
public class SubjectTransactionSourceTests : TransactionTestBase
{
    [Fact]
    public void SetSource_StoresSourceReference()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();

        property.SetSource(sourceMock);

        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(sourceMock, retrievedSource);
    }

    [Fact]
    public void TryGetSource_WhenNoSourceSet_ReturnsFalse()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));

        var result = property.TryGetSource(out var source);

        Assert.False(result);
        Assert.Null(source);
    }

    [Fact]
    public void SetSource_WhenCalledMultipleTimes_ReplacesReference()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var source1Mock = Mock.Of<ISubjectSource>();
        var source2Mock = Mock.Of<ISubjectSource>();

        property.SetSource(source1Mock);
        property.SetSource(source2Mock);

        Assert.True(property.TryGetSource(out var retrievedSource));
        Assert.Same(source2Mock, retrievedSource);
        Assert.NotSame(source1Mock, retrievedSource);
    }

    [Fact]
    public void RemoveSource_ClearsSourceReference()
    {
        var context = CreateContext();
        var person = new Person(context);
        var property = new PropertyReference(person, nameof(Person.FirstName));
        var sourceMock = Mock.Of<ISubjectSource>();
        property.SetSource(sourceMock);

        property.RemoveSource();

        Assert.False(property.TryGetSource(out _));
    }

    [Fact]
    public async Task CommitAsync_WithSourceBoundProperty_WritesToSource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateSucceedingSource();

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            await transaction.CommitAsync(CancellationToken.None);
        }

        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 1),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal("John", person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithSourceWriteFailure_ThrowsAggregateException()
    {
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = CreateFailingSource("Write failed");

        var property = new PropertyReference(person, nameof(Person.FirstName));
        property.SetSource(sourceMock.Object);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Single(exception.InnerExceptions);
            Assert.IsType<SourceWriteException>(exception.InnerExceptions[0]);
        }

        Assert.Null(person.FirstName);
    }

    [Fact]
    public async Task CommitAsync_WithMixedSourceAndLocal_AppliesLocalAndSuccessfulSource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var failingSource = CreateFailingSource("Write failed");

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        firstNameProp.SetSource(failingSource.Object);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => transaction.CommitAsync(CancellationToken.None));

            Assert.Single(exception.InnerExceptions);
        }

        Assert.Null(person.FirstName);
        Assert.Equal("Doe", person.LastName);
    }

    [Fact]
    public async Task CommitAsync_WithMultipleSources_GroupsBySource()
    {
        var context = CreateContext();
        var person = new Person(context);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source1Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source2Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(source1Mock.Object);
        lastNameProp.SetSource(source2Mock.Object);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            person.LastName = "Doe";
            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal(1, source1Writes[0]);
        Assert.Equal(1, source2Writes[0]);
    }

    [Fact]
    public async Task CommitAsync_WhenCancelled_PropagatesCancellation()
    {
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Returns<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            });

        new PropertyReference(person, nameof(Person.FirstName)).SetSource(sourceMock.Object);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";

            var exception = await Assert.ThrowsAsync<AggregateException>(
                () => transaction.CommitAsync(cts.Token));

            var sourceWriteException = Assert.Single(exception.InnerExceptions.OfType<SourceWriteException>());
            Assert.IsType<OperationCanceledException>(sourceWriteException.InnerException);
        }
    }

    [Fact]
    public async Task Integration_WithMockSource_VerifyWriteChangesAsyncCalled()
    {
        var context = CreateContext();
        var person = new Person(context);

        var sourceMock = new Mock<ISubjectSource>();
        sourceMock.Setup(s => s.WriteBatchSize).Returns(0);

        var capturedChanges = new List<SubjectPropertyChange>();
        sourceMock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) =>
            {
                foreach (var change in changes.Span)
                {
                    capturedChanges.Add(change);
                }
            })
            .Returns(ValueTask.CompletedTask);

        var firstNameProp = new PropertyReference(person, nameof(Person.FirstName));
        var lastNameProp = new PropertyReference(person, nameof(Person.LastName));
        firstNameProp.SetSource(sourceMock.Object);
        lastNameProp.SetSource(sourceMock.Object);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person.FirstName = "John";
            person.LastName = "Doe";

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Equal(2, capturedChanges.Count);
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.FirstName) && c.GetNewValue<string>() == "John");
        Assert.Contains(capturedChanges, c => c.Property.Metadata.Name == nameof(Person.LastName) && c.GetNewValue<string>() == "Doe");

        sourceMock.Verify(s => s.WriteChangesAsync(
            It.Is<ReadOnlyMemory<SubjectPropertyChange>>(m => m.Length == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CommitAsync_WithMultipleContexts_ResolvesCallbacksPerContext()
    {
        var context1 = CreateContext();
        var context2 = CreateContext();

        var person1 = new Person(context1);
        var person2 = new Person(context2);

        var source1Writes = new List<int>();
        var source1Mock = new Mock<ISubjectSource>();
        source1Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source1Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source1Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        var source2Writes = new List<int>();
        var source2Mock = new Mock<ISubjectSource>();
        source2Mock.Setup(s => s.WriteBatchSize).Returns(0);
        source2Mock.Setup(s => s.WriteChangesAsync(It.IsAny<ReadOnlyMemory<SubjectPropertyChange>>(), It.IsAny<CancellationToken>()))
            .Callback<ReadOnlyMemory<SubjectPropertyChange>, CancellationToken>((changes, _) => source2Writes.Add(changes.Length))
            .Returns(ValueTask.CompletedTask);

        new PropertyReference(person1, nameof(Person.FirstName)).SetSource(source1Mock.Object);
        new PropertyReference(person2, nameof(Person.FirstName)).SetSource(source2Mock.Object);

        using (var transaction = SubjectTransaction.BeginTransaction())
        {
            person1.FirstName = "John";
            person2.FirstName = "Jane";

            await transaction.CommitAsync(CancellationToken.None);
        }

        Assert.Single(source1Writes);
        Assert.Single(source2Writes);
        Assert.Equal("John", person1.FirstName);
        Assert.Equal("Jane", person2.FirstName);
    }
}
