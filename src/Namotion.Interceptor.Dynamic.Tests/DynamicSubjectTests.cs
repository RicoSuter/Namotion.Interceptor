using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Dynamic.Tests;

public interface IMotor
{
    int Speed { get; set; }
}

public interface ISensor
{
    int Temperature { get; set; }
}

[InterceptorSubject]
public partial class Motor : IMotor
{
    public Motor()
    {
        Speed = 100;
    }
    
    public partial int Speed { get; set; }
}

public class DynamicSubjectTests
{
    [Fact]
    public void WhenCreatingDynamicSubject_ThenItImplementsInterfaces()
    {
        // Act
        var subject = DynamicSubjectFactory.CreateDynamicSubject(null, typeof(IMotor), typeof(ISensor));
        var motor = (IMotor)subject;
        var sensor = (ISensor)subject;

        // Assert
        motor.Speed = 100;
        sensor.Temperature = 25;

        Assert.Equal(100, motor.Speed);
        Assert.Equal(25, sensor.Temperature);
    }
    
    [Fact]
    public void WhenCreatingDynamicSubject_ThenRegistryKnowsProperties()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        var subject = DynamicSubjectFactory.CreateDynamicSubject(context, typeof(IMotor), typeof(ISensor));

        // Act
        var registeredSubject = subject.TryGetRegisteredSubject()!;
        var properties = registeredSubject.Properties;

        // Assert
        Assert.Contains(properties, p => p.Name == "Speed");
        Assert.Contains(properties, p => p.Name == "Temperature");
    }
    
    [Fact]
    public Task WhenInterceptingDynamicSubject_ThenTheyAreCalled()
    {
        // Act
        var logs = new List<string>();
        
        var context = InterceptorSubjectContext
            .Create()
            .WithService(() => new TestInterceptor("a", logs), _ => false)
            .WithService(() => new TestInterceptor("b", logs), _ => false);

        var subject = DynamicSubjectFactory.CreateDynamicSubject(context, typeof(IMotor), typeof(ISensor));
        var motor = (IMotor)subject;
        var sensor = (ISensor)subject;

        // Act
        motor.Speed = 100;
        sensor.Temperature = 25;
        var speed = motor.Speed;
        var temperature = sensor.Temperature;

        ((IInterceptorSubject)subject).Context.RemoveFallbackContext(context);

        // Assert & Act (read)
        Assert.Equal(102, motor.Speed);
        Assert.Equal(27, sensor.Temperature);
        
        // Assert
        return Verify(logs);
    }
    
    [Fact]
    public void WhenCreatingDynamicSubjectForClass_ThenItImplementsInterfaces()
    {
        // Arrange
        var motor = DynamicSubjectFactory.CreateSubject<Motor>(null, typeof(IMotor), typeof(ISensor));
        var motorFromInterface = (IMotor)motor;
        var sensor = (ISensor)motor;

        // Act
        sensor.Temperature = 5;
        
        // Assert
        // 100 from class (direct call on class)
        Assert.Equal(100, motor.Speed);

        // 100 from class (should redirect interface call to class)
        Assert.Equal(100, motorFromInterface.Speed); 
        
        // 5 from dynamic store (interface not implemented)
        Assert.Equal(5, sensor.Temperature);
    }

    public class TestInterceptor : IReadInterceptor, IWriteInterceptor, ILifecycleInterceptor
    {
        private readonly string _name;
        private readonly List<string> _logs;

        public TestInterceptor(string name, List<string> logs)
        {
            _name = name;
            _logs = logs;
        }

        public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ReadInterceptionDelegate<TProperty> next)
        {
            _logs.Add($"{_name}: Before read {context.Property.Name}");
            var result = next(ref context);
            _logs.Add($"{_name}: After read {context.Property.Name}");
            return result;
        }

        public bool ShouldInterceptWrite => true;

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            _logs.Add($"{_name}: Before write {context.Property.Name}");
            context.NewValue = (TProperty)(object)((int)((object)context.NewValue!) + 1);
            next(ref context);
            _logs.Add($"{_name}: After write {context.Property.Name}");
        }

        public void AttachTo(IInterceptorSubject subject)
        {
            _logs.Add($"{_name}: Attached");
        }

        public void DetachFrom(IInterceptorSubject subject)
        {
            _logs.Add($"{_name}: Detached");
        }
    }
}