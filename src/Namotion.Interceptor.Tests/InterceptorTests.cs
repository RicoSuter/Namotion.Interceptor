using Namotion.Interceptor.Interceptors;

namespace Namotion.Interceptor.Tests;

public class InterceptorTests
{
    private sealed class PostTerminalMutationInterceptor : IWriteInterceptor
    {
        public object? FinalValue { get; private set; }

        public object? UnwoundValue { get; private set; }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            context.NewValue = (TProperty)(object)99;
            UnwoundValue = context.NewValue;
            FinalValue = context.GetFinalValue();
        }
    }

    private sealed class DoubleNextInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            next(ref context);
        }
    }

    private sealed class VetoingWriteInterceptor(bool forgeIsWritten = false) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            context.IsWritten = forgeIsWritten;
        }
    }

    private sealed class SuppressingWriteInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            context.IsWritten = false;
        }
    }

    private sealed class ThrowingAfterNextInterceptor : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            throw new TestWriteException();
        }
    }

    private sealed class TestWriteException : Exception
    {
    }

    [Fact]
    public Task WhenReadingProperties_ThenInterceptorsAreCalledInTheRightOrder()
    {
        // Arrange
        var logs = new List<string>();
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestReadInterceptor("a", logs), _ => false)
            .WithService(() => new TestReadInterceptor("b", logs), _ => false);
        
        var car = new Car(context);

        // Act
        var speed = car.Speed;

        // Assert
        return Verify(logs);
    }

    public class TestReadInterceptor : IReadInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        public TestReadInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext<TProperty> context, ReadInterceptionDelegate<TProperty> next)
        {
            _logs.Add($"{_name}: Before read {context.Property.Name}");
            var result = next(ref context);
            _logs.Add($"{_name}: After read {context.Property.Name}");
            return result;
        }
    }
    
    [Fact]
    public Task WhenWritingProperties_ThenInterceptorsAreCalledInTheRightOrder()
    {
        // Arrange
        var logs = new List<string>();
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestWriteInterceptor("a", logs), _ => false)
            .WithService(() => new TestWriteInterceptor("b", logs), _ => false);
        
        var car = new Car(context);

        // Act
        car.Speed = 5;

        // Assert
        Assert.Equal(7, car.Speed); // both interceptors added 1
        return Verify(logs);
    }

    [Fact]
    public void WhenInterceptorMutatesNewValueAfterNext_ThenOnlyItsUnwindStateChanges()
    {
        // Arrange
        var interceptor = new PostTerminalMutationInterceptor();
        var context = InterceptorSubjectContext.Create().WithService(() => interceptor);
        var car = new Car(context);

        // Act
        car.Speed = 5;

        // Assert
        Assert.Equal(5, car.Speed);
        Assert.Equal(99, interceptor.UnwoundValue);
        Assert.Equal(5, interceptor.FinalValue);
    }

    [Fact]
    public void WhenInterceptorInvokesNextTwice_ThenTheSecondTerminalIsRejected()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithService(() => new DoubleNextInterceptor());
        var car = new Car(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)car).Executor;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => car.Speed = 5);
        Assert.Equal(5, car.Speed);
        Assert.Equal(1, executor.Revision);
    }

    [Fact]
    public void WhenInterceptorVetoesWrite_ThenNoTerminalRevisionIsCreated()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithService(() => new VetoingWriteInterceptor());
        var car = new Car(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)car).Executor;

        // Act
        car.Speed = 5;

        // Assert
        Assert.Equal(0, car.Speed);
        Assert.Equal(0, executor.Revision);
    }

    [Fact]
    public void WhenInterceptorForgesIsWrittenWithoutNext_ThenExecutorReportsNoCommit()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithService(() => new VetoingWriteInterceptor(forgeIsWritten: true));
        var car = new Car(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)car).Executor;
        var storedValue = 0;

        // Act
        var written = executor.SetPropertyValue(nameof(Car.Speed), 5, 0, (_, value) => storedValue = value);

        // Assert
        Assert.False(written);
        Assert.Equal(0, storedValue);
        Assert.Equal(0, executor.Revision);
    }

    [Fact]
    public void WhenInterceptorClearsIsWrittenAfterNext_ThenExecutorReportsTheCommit()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithService(() => new SuppressingWriteInterceptor());
        var car = new Car(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)car).Executor;
        var storedValue = 0;

        // Act
        var written = executor.SetPropertyValue(nameof(Car.Speed), 5, 0, (_, value) => storedValue = value);

        // Assert
        Assert.True(written);
        Assert.Equal(5, storedValue);
        Assert.Equal(1, executor.Revision);
    }

    [Fact]
    public void WhenInterceptorThrowsAfterNext_ThenTheSingleTerminalCommitRemains()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithService(() => new ThrowingAfterNextInterceptor());
        var car = new Car(context);
        var executor = (InterceptorExecutor)((IInterceptorSubject)car).Executor;

        // Act & Assert
        Assert.Throws<TestWriteException>(() => car.Speed = 5);
        Assert.Equal(5, car.Speed);
        Assert.Equal(1, executor.Revision);
    }

    public class TestWriteInterceptor : IWriteInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        public TestWriteInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            _logs.Add($"{_name}: Before write {context.Property.Name}");
            context.NewValue = (TProperty)(object)((int)((object)context.NewValue!) + 1);
            next(ref context);
            _logs.Add($"{_name}: After write {context.Property.Name}");
        }
    }
    
    [Fact]
    public Task WhenAddingAndRemovingContext_ThenTheLifecycleInterceptorIsCalled()
    {
        // Arrange: one lifecycle interceptor per context. A second one is a singleton conflict,
        // because two of them would be competing authorities over the same subjects.
        var logs = new List<string>();

        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestLifecycleInterceptor("a", logs), _ => false);

        // Act
        var car = new Car(context);
        ((IInterceptorSubject)car).DetachFromContext(context);

        // Assert
        return Verify(logs);
    }

    [Fact]
    public void WhenASecondLifecycleInterceptorIsRegistered_ThenTheSingletonContractRejectsIt()
    {
        // Arrange
        var logs = new List<string>();
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestLifecycleInterceptor("a", logs), _ => false);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => context.WithService(() => new TestLifecycleInterceptor("b", logs), _ => false));
    }

    public class TestLifecycleInterceptor : ILifecycleInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        private readonly object _structuralWriteGate = new();

        public void EnterStructuralWriteGate() => Monitor.Enter(_structuralWriteGate);

        public void ExitStructuralWriteGate() => Monitor.Exit(_structuralWriteGate);

        public TestLifecycleInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public bool TryAddProperties(SubjectPropertyRegistration registration)
        {
            registration.Publish();
            return true;
        }

        public void AttachSubjectToContext(IInterceptorSubject subject, IInterceptorSubjectContext context, SubjectAttachmentAnchorKind anchor)
        {
            _logs.Add($"{_name}: Attached");
        }

        public void DetachSubjectFromContext(IInterceptorSubject subject, IInterceptorSubjectContext context)
        {
            _logs.Add($"{_name}: Detached");
        }

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
        }
    }
    
    [Fact]
    public Task WhenReadingMetadata_ThenItShouldBeCorrect()
    {
        // Arrange
        var logs = new List<string>();
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestReadInterceptor("a", logs), _ => false)
            .WithService(() => new TestReadInterceptor("b", logs), _ => false);
        
        var car = new Car(context) as IInterceptorSubject;

        // Act
        var properties = car.Properties;

        // Assert
        return Verify(properties);
    }
}
