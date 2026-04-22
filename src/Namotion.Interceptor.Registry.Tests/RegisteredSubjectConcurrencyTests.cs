using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests;

[InterceptorSubject]
public partial class ConcurrencyHost
{
    public partial string Name { get; set; }
}

public class RegisteredSubjectConcurrencyTests
{
    [Fact]
    public async Task WhenReadersRaceAgainstDynamicPropertyAdds_ThenViewsRemainConsistent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var host = new ConcurrencyHost(context);
        var registered = host.TryGetRegisteredSubject();
        Assert.NotNull(registered);

        const int iterations = 200;
        var stop = false;
        var readerException = null as Exception;

        // Act
        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == false)
                {
                    var properties = registered!.Properties;
                    foreach (var property in properties)
                    {
                        var lookedUp = registered.TryGetProperty(property.Name);
                        Assert.NotNull(lookedUp);
                    }
                }
            }
            catch (Exception exception)
            {
                readerException = exception;
            }
        })).ToArray();

        for (var i = 0; i < iterations; i++)
        {
            registered!.AddProperty<string>(
                $"Dynamic{i}",
                getValue: _ => string.Empty,
                setValue: null);
        }

        Volatile.Write(ref stop, true);
        await Task.WhenAll(readerTasks);

        // Assert
        Assert.Null(readerException);
        Assert.Equal(iterations + 1, registered!.Properties.Length);
    }

    [Fact]
    public async Task WhenReadersRaceAgainstDynamicAttributeAdds_ThenAttributesCacheRemainsConsistent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var host = new ConcurrencyHost(context);
        var registered = host.TryGetRegisteredSubject();
        Assert.NotNull(registered);
        var nameProperty = registered.TryGetProperty(nameof(ConcurrencyHost.Name));
        Assert.NotNull(nameProperty);

        const int iterations = 200;
        var stop = false;
        var readerException = null as Exception;

        // Act
        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == false)
                {
                    var attributes = nameProperty!.Attributes;
                    foreach (var attribute in attributes)
                    {
                        var lookedUp = nameProperty.TryGetAttribute(attribute.AttributeName);
                        Assert.NotNull(lookedUp);
                    }
                }
            }
            catch (Exception exception)
            {
                readerException = exception;
            }
        })).ToArray();

        for (var i = 0; i < iterations; i++)
        {
            nameProperty!.AddAttribute(
                $"Attr{i}",
                typeof(int),
                _ => i,
                setValue: null);
        }

        Volatile.Write(ref stop, true);
        await Task.WhenAll(readerTasks);

        // Assert
        Assert.Null(readerException);
        Assert.Equal(iterations, nameProperty!.Attributes.Length);
    }
}
