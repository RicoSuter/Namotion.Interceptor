using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Tests.Handlers
{
    public class UsePropertyChangedHandlersHandlerTests : ProxyChangedHandlerTestsBase
    {
        [Fact]
        public void Test1()
        {
            // Arrange
            var changes = new List<ProxyChangedHandlerContext>();
            var changeHandler = CreateMockProxyChangedHandler(changes);

            var context = ProxyContext
                .CreateBuilder()
                .WithPropertyChangedHandlers()
                .AddHandler(changeHandler)
                .Build();

            // Act
            var person = new Person();
            person.SetContext(context);

            person.FirstName = "Rico";

            // Assert
            Assert.Contains(changes, c => c.PropertyName == "FirstName" && c.NewValue?.ToString() == "Rico");
        }
    }
}