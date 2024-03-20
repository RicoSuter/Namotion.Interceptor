namespace Namotion.Proxy.Tests.Handlers
{
    public class AutomaticContextAssignmentHandlerTests
    {
        [Fact]
        public void WhenPropertyIsAssigned_ThenContextIsSet()
        {
            // Arrange
            var context = ProxyContext
                .CreateBuilder()
                .WithAutomaticContextAssignment()
                .Build();

            // Act
            var person = new Person(context);
            person.Mother = new Person { FirstName = "Susi" };

            // Assert
            Assert.Equal(context, ((IProxy)person.Mother).Context);
        }

        [Fact]
        public void WhenPropertyWithDeepStructureIsAssigned_ThenChildrenAlsoHaveContext()
        {
            // Arrange
            var context = ProxyContext
                .CreateBuilder()
                .WithAutomaticContextAssignment()
                .Build();

            // Act
            var grandmother = new Person { FirstName = "Grandmother" };

            var person = new Person(context)
            {
                Mother = new Person
                {
                    FirstName = "Susi",
                    Mother = grandmother
                }
            };

            // Assert
            Assert.Equal(context, ((IProxy)person.Mother).Context);
            Assert.Equal(context, ((IProxy)grandmother).Context);
        }
    }
}
