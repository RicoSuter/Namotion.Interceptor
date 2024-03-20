using Namotion.Proxy.Handlers;

namespace Namotion.Proxy.Tests
{
    public class UnitTest1
    {
        [Fact]
        public void Test1()
        {
            // Arrange
            var context = new ProxyContext([
                new ProxyPropertyValueHandler(),
                new AutoProxyContextHandler()
            ]);

            // Act
            var person = new Person();
            context.RegisterProxy(person);
            person.Mother = new Person { FirstName = "Susi" };

            // Assert
            Assert.Equal(context, ((IProxy)person.Mother).Context);
        }
    }

    [GenerateProxy]
    public abstract class PersonBase
    {
        public virtual string FirstName { get; set; }

        public virtual string? LastName { get; set; }

        public virtual Person? Father { get; set; }

        public virtual Person? Mother { get; set; }
    }
}