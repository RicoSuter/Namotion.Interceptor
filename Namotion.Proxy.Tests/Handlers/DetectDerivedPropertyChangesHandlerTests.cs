using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.Tests.Handlers
{
    public class DetectDerivedPropertyChangesHandlerTests
    {
        [Fact]
        public void WhenChangingPropertyWhichIsUsedInDerivedProperty_ThenDerivedPropertyIsChanged()
        {
            // Arrange
            var changes = new List<ProxyChangedHandlerContext>();
            var context = ProxyContext
                .CreateBuilder()
                .WithDerivedPropertyChangeDetection()
                .WithPropertyChangedCallback(changes.Add)
                .Build();

            // Act
            var person = new Person(context);
            person.FirstName = "Rico";
            person.LastName = "Suter";

            // Assert
            Assert.Contains(changes, c =>
                c.PropertyName == nameof(Person.FullName) &&
                c.OldValue?.ToString() == " " &&
                c.NewValue?.ToString() == "Rico ");

            Assert.Contains(changes, c => 
                c.PropertyName == nameof(Person.FullName) &&
                c.OldValue?.ToString() == "Rico " && 
                c.NewValue?.ToString() == "Rico Suter");
        }
    }
}