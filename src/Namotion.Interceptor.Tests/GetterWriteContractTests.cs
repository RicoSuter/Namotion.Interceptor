namespace Namotion.Interceptor.Tests;

#if DEBUG
public class GetterWriteContractTests
{
    [Fact]
    public void WhenGetterBehindTheReadChainWritesASubjectTypedProperty_ThenTheDebugGuardRejectsIt()
    {
        // Arrange: the read terminal is where user getter code (an added property's getter
        // delegate) runs, under SyncRoot when interceptors are present. A structural write from
        // there would invert the lock order (gate, then attachment monitor, then SyncRoot), so it
        // is a contract violation the debug guard rejects.
        var context = InterceptorSubjectContext.Create();
        var subject = new StructuralHolder(context);
        var executor = ((IInterceptorSubject)subject).Executor;

        // Act
        var exception = Record.Exception(() => executor.GetPropertyValue<int>(
            nameof(StructuralHolder.Count),
            _ =>
            {
                subject.Child = new StructuralHolder();
                return 0;
            }));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("getter must not write a subject-typed property", exception.Message);
        Assert.Null(subject.Child);
    }

    [Fact]
    public void WhenGetterBehindTheReadChainWritesAScalarProperty_ThenTheWriteIsAllowed()
    {
        // Arrange: only subject-typed writes are the violation; a scalar write from a getter stays
        // outside the contract (it takes no lifecycle gate and no attachment monitor).
        var context = InterceptorSubjectContext.Create();
        var subject = new StructuralHolder(context);
        var executor = ((IInterceptorSubject)subject).Executor;

        // Act
        var result = executor.GetPropertyValue<int>(
            nameof(StructuralHolder.Count),
            _ =>
            {
                subject.Count = 7;
                return 7;
            });

        // Assert
        Assert.Equal(7, result);
        Assert.Equal(7, subject.Count);
    }
}
#endif
