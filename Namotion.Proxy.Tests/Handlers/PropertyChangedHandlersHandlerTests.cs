using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Tests.Handlers
{
    public class PropertyChangedHandlersHandlerTests
    {
        [Fact]
        public void WhenPropertyIsChanged_ThenChangeHandlerIsTriggered()
        {
            // Arrange
            var changes = new List<ProxyChangedHandlerContext>();
            var context = ProxyContext
                .CreateBuilder()
                .WithPropertyChangedHandlers()
                .WithPropertyChangedCallback(changes.Add)
                .Build();

            // Act
            var person = new Person(context);
            person.FirstName = "Rico";

            // Assert
            Assert.Contains(changes, c => 
                c.PropertyName == "FirstName" &&
                c.OldValue is null &&
                c.NewValue?.ToString() == "Rico");
        }
    }
}