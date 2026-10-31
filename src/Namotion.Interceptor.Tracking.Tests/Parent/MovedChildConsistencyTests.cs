using System.Collections;
using System.Collections.Concurrent;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Parent;

/// <summary>
/// Checks that every copy of a parent relationship agrees with the value after a sequence of structural writes that
/// keep, move, re-key, repeat and drop children: the tracked parents, the registry parents and the registry children.
/// A holder keeps every car in the graph, so a parent entry a removal leaves behind stays visible in the registry.
/// </summary>
public class MovedChildConsistencyTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents();
    }

    private static Car[] CreateHeldCars(IInterceptorSubjectContext context, int count)
    {
        var cars = Enumerable.Range(0, count).Select(i => new Car { Name = "Car" + i }).ToArray();
        _ = new Garage(context) { Cars = cars };
        return cars;
    }

    [Fact]
    public void WhenCollectionGoesThroughMixedWrites_ThenEveryCopyMatchesTheValueAfterEachStep()
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 5);
        var garage = new Garage(context);
        Car[][] steps =
        [
            [cars[0], cars[1], cars[2]],
            [cars[0], cars[4], cars[2]],
            [cars[0], cars[4], cars[2], cars[0]],
            [cars[2], cars[0]],
            [cars[2], cars[0], cars[3], cars[1]],
            [cars[3], cars[1], cars[3]],
            [cars[4], cars[3], cars[1], cars[3], cars[4]],
            [cars[4], cars[3], cars[2], cars[1], cars[0]],
            [cars[3], cars[3]],
            [cars[1], cars[3]],
            [cars[3], cars[1], cars[3]],
            [cars[1], cars[3]],
            []
        ];

        foreach (var step in steps)
        {
            // Act
            garage.Cars = step;

            // Assert
            AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        }
    }

    [Fact]
    public void WhenDictionaryGoesThroughMixedWrites_ThenEveryCopyMatchesTheValueAfterEachStep()
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 5);
        var garage = new Garage(context);

        // A removed entry frees its slot, so the entry added after it enumerates in its place.
        var reusedSlot = new Dictionary<string, Car> { ["A"] = cars[3], ["B"] = cars[1], ["C"] = cars[2] };
        reusedSlot.Remove("B");
        reusedSlot["Z"] = cars[0];

        Dictionary<string, Car>[] steps =
        [
            new() { ["A"] = cars[0], ["B"] = cars[1], ["C"] = cars[2] },
            new() { ["X"] = cars[0], ["B"] = cars[1], ["C"] = cars[2] },
            reusedSlot,
            new() { ["C"] = cars[2], ["A"] = cars[2], ["Q"] = cars[4] },
            new() { ["Q"] = cars[4] },
            new() { ["Q"] = cars[2], ["C"] = cars[4] },
            new() { ["First"] = cars[3], ["Second"] = cars[3] },
            new() { ["Second"] = cars[3] },
            new() { ["First"] = cars[3], ["Second"] = cars[3] },
            new() { ["Other"] = cars[1], ["Second"] = cars[3] },
            new()
        ];

        foreach (var step in steps)
        {
            // Act
            garage.CarsByName = step;

            // Assert
            AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
        }
    }

    [Fact]
    public void WhenBroadPropertySwitchesShapes_ThenEveryCopyMatchesTheValueAfterEachStep()
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 3);
        var holder = new BroadlyDeclaredHolder(context);
        object?[] steps =
        [
            new Dictionary<string, Car> { ["K"] = cars[0], ["J"] = cars[1] },
            new List<Car> { cars[1], cars[0] },
            cars[0],
            new Dictionary<string, Car> { ["Z"] = cars[0] },
            new List<Car> { cars[0], cars[0], cars[1] },
            cars[1],
            new Dictionary<int, Car> { [0] = cars[1], [1] = cars[2] },
            new Dictionary<int, Car> { [0] = cars[1], [5] = cars[2] },
            cars[2],
            new List<Car> { cars[1], cars[2] },
            cars.Select(car => car),
            new[] { cars[2], cars[0], cars[1] }.Select(car => car),
            new[,] { { cars[0], cars[1], cars[2] } },
            new[,] { { cars[2], cars[0], cars[1] } },
            null
        ];

        foreach (var step in steps)
        {
            // Act
            holder.BroadCars = step;

            // Assert
            AssertEveryCopyMatchesValue(holder, nameof(BroadlyDeclaredHolder.BroadCars), cars);
        }
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenEntryIsReKeyedWhileParentsAreRead_ThenReadersAlwaysSeeOneEntry()
    {
        // Arrange
        var car = new Car { Name = "Car" };
        var garage = new Garage(CreateContext())
        {
            CarsByName = new Dictionary<string, Car> { ["A"] = car }
        };

        var property = new PropertyReference(garage, nameof(Garage.CarsByName));
        var registeredCar = car.TryGetRegisteredSubject();
        Assert.NotNull(registeredCar);
        var failures = new ConcurrentQueue<string>();
        using var readerStarted = new ManualResetEventSlim();
        using var writesDone = new ManualResetEventSlim();

        var reader = new Thread(() =>
        {
            try
            {
                readerStarted.Set();
                while (!writesDone.IsSet)
                {
                    var tracked = car.GetParents().Where(parent => parent.Property == property).ToArray();
                    var registered = registeredCar.Parents.Where(parent => parent.Property.Reference == property).ToArray();
                    if (tracked.Length != 1 || registered.Length != 1)
                    {
                        failures.Enqueue($"Tracked: {tracked.Length}, registered: {registered.Length}");
                    }
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception.ToString());
            }
        });

        reader.Start();
        readerStarted.Wait();

        // Act
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            garage.CarsByName = new Dictionary<string, Car> { [iteration % 2 == 0 ? "B" : "A"] = car };
        }

        writesDone.Set();
        reader.Join();

        // Assert
        Assert.Empty(failures);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), [car]);
    }

    [Fact]
    [Trait("Category", "Concurrency")]
    public void WhenWritersShuffleCollectionAndDictionary_ThenEveryCopyMatchesTheValueAfterwards()
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 6);
        var garage = new Garage(context);
        const int writerCount = 4;
        var failures = new ConcurrentQueue<Exception>();
        using var start = new Barrier(writerCount);

        var writers = Enumerable.Range(0, writerCount).Select(seed => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                WriteRandomly(garage, cars, new Random(seed));
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        })).ToList();

        // Act
        writers.ForEach(thread => thread.Start());
        writers.ForEach(thread => thread.Join());

        // Assert
        Assert.Empty(failures);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
    }

    private static void WriteRandomly(Garage garage, Car[] cars, Random random)
    {
        for (var iteration = 0; iteration < 3_000; iteration++)
        {
            var picked = cars.OrderBy(_ => random.Next()).Take(random.Next(cars.Length + 1)).ToList();
            if (random.Next(2) == 0)
            {
                // A repeated item exercises the first-position rule.
                garage.Cars = picked.Count > 0 ? [.. picked, picked[0]] : picked;
            }
            else
            {
                garage.CarsByName = picked.ToDictionary(car => car.Name + random.Next(3), car => car);
            }
        }
    }

    /// <summary>
    /// Asserts that the registry children of the property are the distinct subjects of its value, each with its first
    /// position or key, in the order of a collection (a dictionary's children keep their attach order), that the
    /// tracked and the registry parents of each carry the same index, and that no former child keeps a parent entry.
    /// </summary>
    private static void AssertEveryCopyMatchesValue(
        IInterceptorSubject parent, string propertyName, IEnumerable<IInterceptorSubject> candidates)
    {
        var registeredProperty = parent.TryGetRegisteredSubject()?.TryGetProperty(propertyName);
        Assert.NotNull(registeredProperty);
        var property = new PropertyReference(parent, propertyName);
        var expected = GetFirstOccurrences(registeredProperty.GetValue());
        var actual = registeredProperty.Children.Select(child => (child.Subject, child.Index)).ToList();
        if (registeredProperty.GetValue() is IDictionary)
        {
            expected = expected.OrderBy(child => child.Index?.ToString(), StringComparer.Ordinal).ToList();
            actual = actual.OrderBy(child => child.Index?.ToString(), StringComparer.Ordinal).ToList();
        }

        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Same(expected[i].Subject, actual[i].Subject);
            Assert.Equal(expected[i].Index, actual[i].Index);
        }

        foreach (var candidate in candidates)
        {
            var expectedIndices = expected
                .Where(child => ReferenceEquals(child.Subject, candidate))
                .Select(child => child.Index)
                .ToArray();

            Assert.Equal(expectedIndices, candidate.GetParents()
                .Where(entry => entry.Property == property)
                .Select(entry => entry.Index));

            var registeredParents = candidate.TryGetRegisteredSubject()?.Parents ?? [];
            Assert.Equal(expectedIndices, registeredParents
                .Where(entry => entry.Property.Reference == property)
                .Select(entry => entry.Index));
        }
    }

    private static List<(IInterceptorSubject Subject, object? Index)> GetFirstOccurrences(object? value)
    {
        var children = new List<(IInterceptorSubject Subject, object? Index)>();
        var seen = new HashSet<IInterceptorSubject>(ReferenceEqualityComparer.Instance);
        switch (value)
        {
            case IInterceptorSubject subject:
                children.Add((subject, null));
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subject && seen.Add(subject))
                        children.Add((subject, entry.Key));
                }
                break;

            case IEnumerable enumerable:
                var position = 0;
                foreach (var item in enumerable)
                {
                    if (item is IInterceptorSubject subject && seen.Add(subject))
                        children.Add((subject, position));

                    position++;
                }
                break;
        }

        return children;
    }
}
