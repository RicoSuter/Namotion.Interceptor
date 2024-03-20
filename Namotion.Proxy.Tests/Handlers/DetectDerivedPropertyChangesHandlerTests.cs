using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Tests.Handlers
{
    public class DetectDerivedPropertyChangesHandlerTests : ProxyChangedHandlerTestsBase
    {
        [Fact]
        public void Test1()
        {
            // Arrange
            var changes = new List<ProxyChangedHandlerContext>();
            var changeHandler = CreateMockProxyChangedHandler(changes);

            var context = ProxyContext
                .CreateBuilder()
                .WithDerivedPropertyChangeDetection(initiallyReadAllProperties: true)
                .AddHandler(changeHandler)
                .Build();

            // Act
            var person = new Person();
            person.SetContext(context);
            person.FirstName = "Rico";
            person.LastName = "Suter";

            // Assert
            Assert.Contains(changes, c => c.PropertyName == nameof(Person.FullName));
        }
    }
}