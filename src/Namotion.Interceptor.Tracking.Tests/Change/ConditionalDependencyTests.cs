using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class ConditionalDependencyTests
{
    [Fact]
    public void WhenConditionChanges_ThenDependencySetUpdates()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new ConditionalPerson(context)
        {
            UseFirstName = false,
            FirstName = "Alice",
            LastName = "Bob"
        };

        var displayProperty = new PropertyReference(person, nameof(ConditionalPerson.Display));
        var firstNameProperty = new PropertyReference(person, nameof(ConditionalPerson.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(ConditionalPerson.LastName));
        var useFirstNameProperty = new PropertyReference(person, nameof(ConditionalPerson.UseFirstName));

        // Initial state: Display depends on [UseFirstName, LastName]
        Assert.Equal("Bob", person.Display);
        Assert.Contains(lastNameProperty, displayProperty.GetRequiredProperties().ToArray());
        Assert.Contains(useFirstNameProperty, displayProperty.GetRequiredProperties().ToArray());

        // Act - Switch condition
        person.UseFirstName = true;

        // Assert - Display depends on [UseFirstName, FirstName] now
        Assert.Equal("Alice", person.Display);
        Assert.Contains(firstNameProperty, displayProperty.GetRequiredProperties().ToArray());
        Assert.Contains(useFirstNameProperty, displayProperty.GetRequiredProperties().ToArray());
        Assert.DoesNotContain(lastNameProperty, displayProperty.GetRequiredProperties().ToArray());
    }

    [Fact]
    public void WhenConditionChanges_ThenOldDependencyBacklinksAreRemoved()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new ConditionalPerson(context)
        {
            UseFirstName = false,
            FirstName = "Alice",
            LastName = "Bob"
        };

        var displayProperty = new PropertyReference(person, nameof(ConditionalPerson.Display));
        var firstNameProperty = new PropertyReference(person, nameof(ConditionalPerson.FirstName));
        var lastNameProperty = new PropertyReference(person, nameof(ConditionalPerson.LastName));

        // Initially LastName has Display in its UsedByProperties
        Assert.Contains(displayProperty, lastNameProperty.GetUsedByProperties().Items.ToArray());

        // Act - Switch to FirstName
        person.UseFirstName = true;

        // Assert - LastName no longer has Display as a dependent
        Assert.DoesNotContain(displayProperty, lastNameProperty.GetUsedByProperties().Items.ToArray());

        // FirstName now has Display as a dependent
        Assert.Contains(displayProperty, firstNameProperty.GetUsedByProperties().Items.ToArray());
    }

    [Fact]
    public void WhenConditionChangesBack_ThenDependenciesRevert()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithDerivedPropertyChangeDetection()
            .WithLifecycle();

        var person = new ConditionalPerson(context)
        {
            UseFirstName = false,
            FirstName = "Alice",
            LastName = "Bob"
        };

        // Act - Switch to FirstName then back
        person.UseFirstName = true;
        Assert.Equal("Alice", person.Display);

        person.UseFirstName = false;

        // Assert - Back to LastName dependency
        Assert.Equal("Bob", person.Display);

        var displayProperty = new PropertyReference(person, nameof(ConditionalPerson.Display));
        var lastNameProperty = new PropertyReference(person, nameof(ConditionalPerson.LastName));

        Assert.Contains(lastNameProperty, displayProperty.GetRequiredProperties().ToArray());
    }

    [Fact]
    public async Task WhenConditionAndDependencyWrittenConcurrently_ThenValueConvergesToCorrectState()
    {
        // Stress test: concurrent writes to the condition flag and the dependencies.
        // After all writes complete, the derived property must reflect the final state.

        const int iterations = 100;

        for (var i = 0; i < iterations; i++)
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithFullPropertyTracking();

            var person = new ConditionalPerson(context)
            {
                UseFirstName = false,
                FirstName = "Alice",
                LastName = "Bob"
            };

            var barrier = new Barrier(3);

            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.UseFirstName = true;
            });

            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.FirstName = "Charlie";
            });

            var t3 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                person.LastName = "David";
            });

            await Task.WhenAll(t1, t2, t3);

            // Final state must be consistent: Display reflects current UseFirstName + current name
            var expected = person.UseFirstName
                ? (person.FirstName ?? "")
                : (person.LastName ?? "");

            Assert.Equal(expected, person.Display);
        }
    }
}
