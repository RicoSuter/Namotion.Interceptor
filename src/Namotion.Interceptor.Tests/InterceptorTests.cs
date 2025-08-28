namespace Namotion.Interceptor.Tests;

public class InterceptorTests
{
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

        public TProperty ReadProperty<TProperty>(ref ReadPropertyInterception context, ReadInterceptionFunc<TProperty> next)
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

    public class TestWriteInterceptor : IWriteInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        public TestWriteInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public void WriteProperty<TProperty>(ref WritePropertyInterception<TProperty> context, WriteInterceptionAction<TProperty> next)
        {
            _logs.Add($"{_name}: Before write {context.Property.Name}");
            context.NewValue = (TProperty)(object)((int)((object)context.NewValue!) + 1);
            next(ref context);
            _logs.Add($"{_name}: After write {context.Property.Name}");
        }
    }
}