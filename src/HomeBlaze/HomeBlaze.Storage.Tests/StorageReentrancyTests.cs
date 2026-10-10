using System.Text;
using System.Text.Json;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Files;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// Calls back into the storage from code that runs on its worker. Such a call has to run as part of the item
/// that makes it: queued, it would wait for the item that waits for it, and the worker would never run again.
/// </summary>
public class StorageReentrancyTests : StorageTestBase
{
    // A call that waits for itself never completes, so the wait for it ends the test instead.
    private static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhenLoadOfFileWritesBlobDuringPass_ThenPassCompletesAndTreeMatchesStorage()
    {
        // Arrange
        WriteFile("Notes.md", "first");
        var storage = await ConnectAsync();
        var notes = Assert.IsType<MarkdownFile>(storage.Children["Notes.md"]);
        WriteFile("Data.gated");
        GatedFile.RunOnNextLoad((file, cancellationToken) =>
            WriteBlobAsync(file.Storage, "Notes.md", "written by the load", cancellationToken));

        // Act
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Data.gated", "Notes.md"], storage.Children.Keys.Order());
        Assert.Same(notes, storage.Children["Notes.md"]);
        Assert.Equal("written by the load", File.ReadAllText(GetFullPath("Notes.md")));
        Assert.Equal("written by the load", notes.Content);
    }

    [Fact]
    public async Task WhenLoadOfFileAddsSubjectDuringPass_ThenPassCompletesAndSubjectIsPlaced()
    {
        // Arrange
        var storage = await ConnectAsync();
        var motor = CreateMotor("Added by the load");
        var detaches = CountDetachesOf(motor);
        WriteFile("Data.gated");
        GatedFile.RunOnNextLoad((file, cancellationToken) =>
            file.Storage.AddSubjectAsync("Devices/Motor.json", motor, cancellationToken));

        // Act
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Data.gated", "Devices"], storage.Children.Keys.Order());
        Assert.Same(motor, Assert.IsType<VirtualFolder>(storage.Children["Devices"]).Children["Motor"]);
        Assert.Contains("Added by the load", File.ReadAllText(GetFullPath("Devices/Motor.json")));
        Assert.Equal(0, detaches.Count);
    }

    [Fact]
    public async Task WhenRefreshAfterBlobWriteAddsSubject_ThenWriteCompletesAndSubjectIsPlaced()
    {
        // Arrange
        WriteFile("Data.gated", "first");
        var storage = await ConnectAsync();
        var file = Assert.IsType<GatedFile>(storage.Children["Data.gated"]);
        var motor = CreateMotor("Added by the refresh");
        GatedFile.RunOnNextLoad((loaded, cancellationToken) =>
            loaded.Storage.AddSubjectAsync("Motor.json", motor, cancellationToken));

        // Act
        await WriteBlobAsync(storage, "Data.gated", "second version", CancellationToken.None).WaitAsync(DeadlockTimeout);
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Data.gated", "Motor"], storage.Children.Keys.Order());
        Assert.Same(motor, storage.Children["Motor"]);
        Assert.Equal("second version", file.Content);
    }

    [Fact]
    public async Task WhenConfigurationOfSubjectAddsSubjectDuringPass_ThenPassCompletesAndBothArePlaced()
    {
        // Arrange
        WriteFile("Valve.json", CallbackSubject.Serialize("Valve"));
        var storage = await ConnectAsync();
        var valve = Assert.IsType<CallbackSubject>(storage.Children["Valve"]);
        var motor = CreateMotor("Added by the configuration");
        CallbackSubject.RunOnNextApply((_, cancellationToken) =>
            storage.AddSubjectAsync("Devices/Motor.json", motor, cancellationToken));
        WriteFile("Valve.json", CallbackSubject.Serialize("Valve, changed"));

        // Act
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Devices", "Valve"], storage.Children.Keys.Order());
        Assert.Same(valve, storage.Children["Valve"]);
        Assert.Equal("Valve, changed", valve.Name);
        Assert.Same(motor, Assert.IsType<VirtualFolder>(storage.Children["Devices"]).Children["Motor"]);
    }

    [Fact]
    public async Task WhenMarkdownFileSavesEmbeddedSubjectDuringPass_ThenPassCompletesAndPageHoldsWhatWasSaved()
    {
        // Arrange
        WriteFile("Page.md", $"# Page\n\n```subject(valve)\n{CallbackSubject.Serialize("first")}\n```\n");
        var storage = await ConnectAsync();
        var page = Assert.IsType<MarkdownFile>(storage.Children["Page.md"]);
        var valve = Assert.IsType<CallbackSubject>(page.Children["valve"]);
        valve.Name = "saved during the pass";
        WriteFile("Data.gated");
        GatedFile.RunOnNextLoad((_, cancellationToken) => page.WriteConfigurationAsync(valve, cancellationToken));

        // Act
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);
        await storage.ReconcileAsync().WaitAsync(DeadlockTimeout);

        // Assert
        Assert.Equal(StorageStatus.Connected, storage.Status);
        Assert.Equal(["Data.gated", "Page.md"], storage.Children.Keys.Order());
        Assert.Same(page, storage.Children["Page.md"]);
        Assert.Contains("saved during the pass", File.ReadAllText(GetFullPath("Page.md")));
        Assert.Equal(File.ReadAllText(GetFullPath("Page.md")), page.Content);
        Assert.Equal("saved during the pass", Assert.IsType<CallbackSubject>(page.Children["valve"]).Name);
    }

    [Fact]
    public async Task WhenPassOfParentReconnectsNestedStorageWhileItWorks_ThenBothCompleteAndNestedTreeMatchesItsNewStorage()
    {
        // Arrange
        var nestedDirectory = CreateTemporaryDirectory();
        var otherDirectory = CreateTemporaryDirectory();
        File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Old.md"), "content");
        File.WriteAllText(Path.Combine(otherDirectory.FullName, "New.md"), "content");
        WriteFile("Nested.json", SerializeStorage(nestedDirectory.FullName));

        var parent = await ConnectAsync();
        var nested = Assert.IsType<FluentStorageContainer>(parent.Children["Nested"]);
        try
        {
            // Connected here, because no host runs in this test that would start the nested storage.
            await nested.ConnectAsync(CancellationToken.None);
            var childrenBeforeReconnect = nested.Children.Keys.ToList();

            File.WriteAllText(Path.Combine(nestedDirectory.FullName, "Slow.gated"), "content");
            var gate = GatedFile.PauseNextLoad();
            var nestedPass = nested.ReconcileAsync();
            await gate.WhenReachedAsync();
            WriteFile("Nested.json", SerializeStorage(otherDirectory.FullName));

            // Act: the pass of the parent applies the new configuration, which waits for the pass of the nested storage.
            var parentPass = parent.ReconcileAsync();
            await AsyncTestHelpers.WaitUntilAsync(() => nested.Status == StorageStatus.Initializing, WatcherTimeout);
            var parentPassWasWaiting = !parentPass.IsCompleted;
            gate.Release();
            await parentPass.WaitAsync(DeadlockTimeout);
            await nestedPass.WaitAsync(DeadlockTimeout);

            // Assert
            Assert.Equal(["Old.md"], childrenBeforeReconnect);
            Assert.True(parentPassWasWaiting);
            Assert.Equal(StorageStatus.Connected, parent.Status);
            Assert.Same(nested, parent.Children["Nested"]);
            Assert.Equal(StorageStatus.Connected, nested.Status);
            Assert.Equal(["New.md"], nested.Children.Keys);
        }
        finally
        {
            nested.Dispose();
        }
    }

    private static string SerializeStorage(string directory)
        => $$"""
            {
              "$type": "{{typeof(FluentStorageContainer).FullName}}",
              "storageType": "disk",
              "connectionString": {{JsonSerializer.Serialize(directory)}},
              "enableFileWatching": false,
              "reconcileIntervalSeconds": 0
            }
            """;

    private static async Task WriteBlobAsync(
        IStorageContainer storage, string path, string content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        await storage.WriteBlobAsync(path, stream, cancellationToken);
    }
}
