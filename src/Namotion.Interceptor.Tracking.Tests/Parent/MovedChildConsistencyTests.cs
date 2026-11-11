using System.Collections;
using System.Collections.Concurrent;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking.Lifecycle;
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WhenCollectionIsWrittenWithRandomRepeatedCars_ThenEveryCopyMatchesTheValueAfterEachStep(int seed)
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 4);
        var garage = new Garage(context);
        var random = new Random(seed);

        for (var step = 0; step < 2_000; step++)
        {
            // Act
            garage.Cars = Enumerable.Range(0, random.Next(7)).Select(_ => cars[random.Next(cars.Length)]).ToList();

            // Assert
            AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WhenDictionaryIsWrittenWithRandomRepeatedCars_ThenEveryCopyMatchesTheValueAfterEachStep(int seed)
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 3);
        var garage = new Garage(context);
        var random = new Random(seed);
        string[] keys = ["K0", "K1", "K2", "K3", "K4"];

        for (var step = 0; step < 2_000; step++)
        {
            // Act
            garage.CarsByName = keys
                .OrderBy(_ => random.Next())
                .Take(random.Next(keys.Length + 1))
                .ToDictionary(key => key, _ => cars[random.Next(cars.Length)]);

            // Assert
            AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
        }
    }

    [Fact]
    public void WhenKeyEqualsWritesAnotherPropertyWhileTheValueRepeatsACar_ThenEveryCopyMatchesBothValues()
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 2);
        var oldKey = new HookedKey("A");
        var holder = new BroadlyDeclaredHolder(context)
        {
            BroadCars = new Dictionary<HookedKey, Car> { [oldKey] = cars[0], [new HookedKey("B")] = cars[0] }
        };

        var nestedWriteRan = false;
        oldKey.OnEquals = () =>
        {
            holder.PreciseCars = new Dictionary<string, Car> { ["X"] = cars[1], ["Y"] = cars[1], ["Z"] = cars[0] };
            nestedWriteRan = true;
        };

        // Act: the old key is compared with the new key "A" while the places of the repeated car are matched.
        holder.BroadCars = new Dictionary<HookedKey, Car>
        {
            [new HookedKey("A")] = cars[0],
            [new HookedKey("C")] = cars[0],
            [new HookedKey("D")] = cars[1]
        };

        // Assert
        Assert.True(nestedWriteRan);
        AssertEveryCopyMatchesValue(holder, nameof(BroadlyDeclaredHolder.BroadCars), cars);
        AssertEveryCopyMatchesValue(holder, nameof(BroadlyDeclaredHolder.PreciseCars), cars);
    }

    [Fact]
    public void WhenHandlerRewritesALaterPropertyWhileTheAttachRepeatsCars_ThenEveryCopyMatchesBothValues()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.Cars));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 1);
        var car = cars[0];
        var garage = new Garage
        {
            Cars = [car, car],
            CarsByName = new Dictionary<string, Car> { ["A"] = car, ["B"] = car }
        };
        handler.NestedWrite = () => garage.CarsByName = new Dictionary<string, Car> { ["New"] = car };

        // Act: the attach of the car to Cars comes first, and the handler then writes CarsByName.
        new BroadlyDeclaredHolder(context).BroadCars = garage;

        // Assert
        Assert.Null(handler.NestedWrite);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
    }

    [Fact]
    public void WhenHandlerRewritesALaterPropertyWhileARootAttaches_ThenEveryCopyMatchesBothValues()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.Cars));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 2);
        var garage = new Garage
        {
            Cars = [cars[0], cars[0]],
            CarsByName = new Dictionary<string, Car> { ["A"] = cars[0] }
        };
        handler.NestedWrite = () => garage.CarsByName = new Dictionary<string, Car>
        {
            ["New"] = cars[1], ["Other"] = cars[1], ["Z"] = cars[0]
        };

        // Act: the garage is a root, so the nested write finds it not attached yet and the attach reads it again later.
        ((IInterceptorSubject)garage).Context.AddFallbackContext(context);

        // Assert
        Assert.Null(handler.NestedWrite);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
    }

    [Fact]
    public void WhenHandlerRewritesAnEarlierPropertyWhileTheAttachRepeatsCars_ThenEveryCopyMatchesBothValues()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.CarsByName));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 2);
        var garage = new Garage
        {
            Cars = [cars[0], cars[0]],
            CarsByName = new Dictionary<string, Car> { ["A"] = cars[0], ["B"] = cars[0] }
        };
        handler.NestedWrite = () => garage.Cars = [cars[1], cars[0], cars[1]];

        // Act: the attach of the car to CarsByName comes after Cars, which the handler then writes.
        new BroadlyDeclaredHolder(context).BroadCars = garage;

        // Assert
        Assert.Null(handler.NestedWrite);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
    }

    [Fact]
    public void WhenHandlerRewritesALaterPropertyKeepingItsRepeatedCarWhileTheAttachRepeatsCars_ThenEveryCopyMatchesBothValues()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.Cars));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 2);
        var garage = new Garage
        {
            Cars = [cars[0], cars[0]],
            CarsByName = new Dictionary<string, Car> { ["A"] = cars[0], ["B"] = cars[0] }
        };
        handler.NestedWrite = () => garage.CarsByName = new Dictionary<string, Car>
        {
            ["A"] = cars[0], ["B"] = cars[0], ["C"] = cars[1]
        };

        // Act: the nested write keeps both keys of the repeated car.
        new BroadlyDeclaredHolder(context).BroadCars = garage;

        // Assert
        Assert.Null(handler.NestedWrite);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
    }

    [Fact]
    public void WhenHandlerDetachesTheSubjectDuringTheAttachOfAProperty_ThenTheRestOfItsAttachAttachesNothing()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.Cars));
        var context = CreateContext().WithService(() => handler);
        var lifecycle = context.TryGetLifecycleInterceptor()!;
        var earlierCar = new Car { Name = "Earlier" };
        var nextCar = new Car { Name = "Next" };
        var laterCar = new Car { Name = "Later" };
        var garage = new Garage
        {
            Cars = [earlierCar, nextCar],
            CarsByName = new Dictionary<string, Car> { ["A"] = laterCar }
        };
        var inner = new BroadlyDeclaredHolder { BroadCars = garage };
        var outer = new BroadlyDeclaredHolder(context);
        handler.NestedWrite = () => inner.BroadCars = null;

        // Act: the attach of the inner holder attaches the garage, whose attach of its first car removes it from the
        // inner holder before the next car of Cars and the car of the later CarsByName.
        outer.BroadCars = inner;

        // Assert
        Assert.Null(handler.NestedWrite);
        Assert.Empty(inner.TryGetRegisteredSubject()!.TryGetProperty(nameof(BroadlyDeclaredHolder.BroadCars))!.Children);
        foreach (var subject in new IInterceptorSubject[] { garage, earlierCar, nextCar, laterCar })
        {
            Assert.False(lifecycle.TryRunWhileAttached(subject, () => { }));
            Assert.Equal(0, subject.GetReferenceCount());
            Assert.Empty(subject.GetParents());
            Assert.Null(subject.TryGetRegisteredSubject());
        }

        // Act: attaching the garage again finds no state its detach left behind.
        inner.BroadCars = garage;

        // Assert
        Assert.All(new[] { earlierCar, nextCar, laterCar }, car => Assert.Equal(1, car.GetReferenceCount()));
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), [earlierCar, nextCar, laterCar]);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), [earlierCar, nextCar, laterCar]);
    }

    [Fact]
    public void WhenKeyEqualsThrowsWhileTheValueRepeatsACar_ThenTheNextWriteIsReconciled()
    {
        // Arrange
        var context = CreateContext();
        var cars = CreateHeldCars(context, 2);
        var oldKey = new HookedKey("A");
        var holder = new BroadlyDeclaredHolder(context)
        {
            BroadCars = new Dictionary<HookedKey, Car> { [oldKey] = cars[0], [new HookedKey("B")] = cars[0] }
        };

        oldKey.OnEquals = () => throw new InvalidOperationException();
        Assert.Throws<InvalidOperationException>(() => holder.BroadCars = new Dictionary<HookedKey, Car>
        {
            [new HookedKey("A")] = cars[0],
            [new HookedKey("C")] = cars[0]
        });

        // Act
        holder.BroadCars = new Dictionary<string, Car> { ["C"] = cars[0], ["D"] = cars[1], ["E"] = cars[0] };

        // Assert
        AssertEveryCopyMatchesValue(holder, nameof(BroadlyDeclaredHolder.BroadCars), cars);
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
        try
        {
            for (var iteration = 0; iteration < 10_000; iteration++)
            {
                garage.CarsByName = new Dictionary<string, Car> { [iteration % 2 == 0 ? "B" : "A"] = car };
            }
        }
        finally
        {
            // The reader is a foreground thread that spins until this is set, so a failing write must still stop it.
            writesDone.Set();
            reader.Join();
        }

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
                // A repeated item exercises the entry per place.
                garage.Cars = picked.Count > 0 ? [.. picked, picked[0]] : picked;
            }
            else
            {
                garage.CarsByName = picked.ToDictionary(car => car.Name + random.Next(3), car => car);
            }
        }
    }

    [Fact]
    public async Task WhenAnotherThreadWritesAPropertyAHandlerWroteDuringTheAttach_ThenThePendingWriteIsReconciled()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.Cars));
        var storedSignal = new StoredValueSignalInterceptor();
        var context = CreateContext().WithService(() => handler).WithService(() => storedSignal);
        var cars = CreateHeldCars(context, 3);
        var garage = new Garage { Cars = [cars[0]] };
        var final = new Dictionary<string, Car> { ["Final"] = cars[2] };
        storedSignal.AwaitedValue = final;
        Task? writer = null;
        handler.NestedWrite = () =>
        {
            garage.CarsByName = new Dictionary<string, Car> { ["Nested"] = cars[1] };

            // The other write stores its value now and reconciles once the attach releases the lock.
            writer = DedicatedThreadTestHelpers.RunOnDedicatedThreadAsync(() => garage.CarsByName = final);
            Assert.True(storedSignal.Stored.Task.Wait(TestTimeouts.Default));
        };

        // Act: the attach of the car to Cars comes first, and the handler then writes CarsByName.
        new BroadlyDeclaredHolder(context).BroadCars = garage;
        Assert.NotNull(writer);
        await writer;

        // Assert
        Assert.Same(final, garage.CarsByName);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
    }

    [Fact]
    public void WhenHandlerRepeatsACarInAnEarlierPropertyWhileARootAttaches_ThenEveryCopyMatchesTheValue()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.CarsByName));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 2);
        var garage = new Garage { Cars = [cars[0]], CarsByName = new Dictionary<string, Car> { ["A"] = cars[1] } };
        handler.NestedWrite = () => garage.Cars = [cars[0], cars[0]];

        // Act: the garage is a root, so the attach visits Cars before the handler writes it.
        ((IInterceptorSubject)garage).Context.AddFallbackContext(context);

        // Assert
        Assert.Null(handler.NestedWrite);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
    }

    [Fact]
    public void WhenHandlerReplacesTheCarOfAnEarlierPropertyWhileARootAttaches_ThenTheNewCarIsAttached()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.CarsByName));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 3);
        var garage = new Garage { Cars = [cars[0]], CarsByName = new Dictionary<string, Car> { ["A"] = cars[1] } };
        handler.NestedWrite = () => garage.Cars = [cars[2]];

        // Act
        ((IInterceptorSubject)garage).Context.AddFallbackContext(context);

        // Assert
        Assert.Null(handler.NestedWrite);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        Assert.Equal(2, cars[2].GetReferenceCount());
    }

    [Fact]
    public void WhenHandlerAttachesTheRootAgainDuringItsAttach_ThenEveryCopyMatchesBothValues()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.Cars));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 2);
        var garage = new Garage { Cars = [cars[0]], CarsByName = new Dictionary<string, Car> { ["A"] = cars[1] } };
        var second = InterceptorSubjectContext.Create();
        second.AddFallbackContext(context);
        handler.NestedWrite = () => ((IInterceptorSubject)garage).Context.AddFallbackContext(second);

        // Act: the second context reaches the same lifecycle interceptor, which attaches the garage again meanwhile.
        ((IInterceptorSubject)garage).Context.AddFallbackContext(context);

        // Assert
        Assert.Null(handler.NestedWrite);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
        Assert.All(cars, car => Assert.Equal(2, car.GetReferenceCount()));
    }

    [Fact]
    public void WhenHandlerDetachesTheRootDuringItsAttach_ThenAttachingItAgainAttachesEveryChild()
    {
        // Arrange
        var handler = new PropertyWritingLifecycleHandler(nameof(Garage.Cars));
        var context = CreateContext().WithService(() => handler);
        var cars = CreateHeldCars(context, 2);
        var garage = new Garage { Cars = [cars[0]], CarsByName = new Dictionary<string, Car> { ["A"] = cars[1] } };
        handler.NestedWrite = () => ((IInterceptorSubject)garage).Context.RemoveFallbackContext(context);
        ((IInterceptorSubject)garage).Context.AddFallbackContext(context);
        var countsAfterDetach = cars.Select(car => car.GetReferenceCount()).ToArray();

        // Act
        ((IInterceptorSubject)garage).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal([1, 1], countsAfterDetach);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.Cars), cars);
        AssertEveryCopyMatchesValue(garage, nameof(Garage.CarsByName), cars);
        Assert.All(cars, car => Assert.Equal(2, car.GetReferenceCount()));
    }

    /// <summary>
    /// Asserts that the registry children of the property are the subjects of its value, one for each position or key,
    /// in the order of a collection (a dictionary's children keep their attach order), that the tracked and the
    /// registry parents of each carry the same indices with the entries of each property adjacent, and that no former
    /// child keeps a parent entry.
    /// </summary>
    private static void AssertEveryCopyMatchesValue(
        IInterceptorSubject parent, string propertyName, IEnumerable<IInterceptorSubject> candidates)
    {
        var registeredProperty = parent.TryGetRegisteredSubject()?.TryGetProperty(propertyName);
        Assert.NotNull(registeredProperty);
        var property = new PropertyReference(parent, propertyName);
        var expected = GetPlaces(registeredProperty.GetValue());
        var expectedChildren = expected;
        var actual = registeredProperty.Children.Select(child => (child.Subject, child.Index)).ToList();
        if (registeredProperty.GetValue() is IDictionary)
        {
            expectedChildren = expected.OrderBy(child => child.Index?.ToString(), StringComparer.Ordinal).ToList();
            actual = actual.OrderBy(child => child.Index?.ToString(), StringComparer.Ordinal).ToList();
        }

        Assert.Equal(expectedChildren.Count, actual.Count);
        for (var i = 0; i < expectedChildren.Count; i++)
        {
            Assert.Same(expectedChildren[i].Subject, actual[i].Subject);
            Assert.Equal(expectedChildren[i].Index, actual[i].Index);
        }

        foreach (var candidate in candidates)
        {
            var expectedIndices = expected
                .Where(child => ReferenceEquals(child.Subject, candidate))
                .Select(child => child.Index)
                .ToArray();

            var trackedParents = candidate.GetParents();
            Assert.Equal(expectedIndices, trackedParents
                .Where(entry => entry.Property == property)
                .Select(entry => entry.Index));
            AssertEntriesOfEachPropertyAreAdjacent(
                trackedParents.Select(entry => entry.Property).ToList(), trackedParents.CountProperties());

            var registeredParents = candidate.TryGetRegisteredSubject()?.Parents ?? [];
            Assert.Equal(expectedIndices, registeredParents
                .Where(entry => entry.Property.Reference == property)
                .Select(entry => entry.Index));
            AssertEntriesOfEachPropertyAreAdjacent(
                registeredParents.Select(entry => entry.Property).ToList(), registeredParents.CountProperties());
        }
    }

    /// <summary>
    /// Asserts that the parent entries of each property follow one another, which <c>CountProperties()</c> relies on, and
    /// that it counts the distinct properties.
    /// </summary>
    private static void AssertEntriesOfEachPropertyAreAdjacent<TProperty>(List<TProperty> properties, int countedProperties)
    {
        var distinctProperties = new List<TProperty>();
        for (var i = 0; i < properties.Count; i++)
        {
            if (i > 0 && EqualityComparer<TProperty>.Default.Equals(properties[i], properties[i - 1]))
            {
                continue;
            }

            Assert.DoesNotContain(properties[i], distinctProperties);
            distinctProperties.Add(properties[i]);
        }

        Assert.Equal(distinctProperties.Count, countedProperties);
    }

    private static List<(IInterceptorSubject Subject, object? Index)> GetPlaces(object? value)
    {
        var children = new List<(IInterceptorSubject Subject, object? Index)>();
        switch (value)
        {
            case IInterceptorSubject subject:
                children.Add((subject, null));
                break;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is IInterceptorSubject subject)
                        children.Add((subject, entry.Key));
                }
                break;

            case IEnumerable enumerable:
                var position = 0;
                foreach (var item in enumerable)
                {
                    if (item is IInterceptorSubject subject)
                        children.Add((subject, position));

                    position++;
                }
                break;
        }

        return children;
    }

    /// <summary>
    /// A lifecycle handler that runs a write once, the first time a subject is attached to the property of the given name.
    /// </summary>
    private sealed class PropertyWritingLifecycleHandler(string propertyName) : ILifecycleHandler
    {
        public Action? NestedWrite { get; set; }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceAdded || change.Property?.Name != propertyName)
                return;

            var nestedWrite = NestedWrite;
            NestedWrite = null;
            nestedWrite?.Invoke();
        }
    }

    /// <summary>
    /// A write interceptor inside the lifecycle interceptor that signals once the awaited value is stored, before the
    /// lifecycle interceptor takes its lock to reconcile it.
    /// </summary>
    [RunsAfter(typeof(LifecycleInterceptor))]
    private sealed class StoredValueSignalInterceptor : IWriteInterceptor
    {
        public object? AwaitedValue { get; set; }

        public TaskCompletionSource Stored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (AwaitedValue is not null && ReferenceEquals(context.NewValue, AwaitedValue))
            {
                Stored.TrySetResult();
            }
        }
    }

    /// <summary>
    /// A key equal to every key of the same name that runs a hook once, the first time its Equals is called.
    /// </summary>
    private sealed class HookedKey(string name)
    {
        public Action? OnEquals { get; set; }

        public override bool Equals(object? obj)
        {
            var onEquals = OnEquals;
            OnEquals = null;
            onEquals?.Invoke();
            return obj is HookedKey other && other.ToString() == name;
        }

        public override int GetHashCode() => name.GetHashCode(StringComparison.Ordinal);

        public override string ToString() => name;
    }
}
