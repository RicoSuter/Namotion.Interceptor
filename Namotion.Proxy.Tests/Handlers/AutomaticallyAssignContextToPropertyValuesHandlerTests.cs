namespace Namotion.Proxy.Tests.Handlers
{
    public class AutomaticallyAssignContextToPropertyValuesHandlerTests
    {
        [Fact]
        public void Test1()
        {
            // Arrange
            var context = ProxyContext
                .CreateBuilder()
                .WithAutomaticContextAssignment()
                .Build();

            // Act
            var person = new Person();
            person.SetContext(context);
            person.Mother = new Person { FirstName = "Susi" };

            // Assert
            Assert.Equal(context, ((IProxy)person.Mother).Context);
        }
    }
}