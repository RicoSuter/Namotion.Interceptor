using FluentStorage;
using FluentStorage.Blobs;
using HomeBlaze.Services;
using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using HomeBlaze.Storage.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Storage.Tests;

public class FileSubjectFactoryTests
{
    [Fact]
    public async Task WhenExtensionMappedFileHasAHostedService_ThenItIsActivated()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ActivatableFile)]);
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);
        var factory = new FileSubjectFactory(new SubjectTypeRegistry(typeProvider), serializer, serviceProvider);

        // Act
        var subject = await factory.CreateFromBlobAsync(
            StorageFactory.Blobs.InMemory(),
            new Mock<IStorageContainer>().Object,
            new Blob("device.activatable"),
            CancellationToken.None);

        // Assert
        Assert.IsType<ActivatableFile>(subject);
        Assert.Single(subject.GetHostedServiceAttachments());
    }

    [Fact]
    public async Task WhenExtensionMappedFileLoadsItsContent_ThenItIsActivatedAfterwards()
    {
        // Arrange
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ContentLoadingActivatableFile)]);
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);
        var factory = new FileSubjectFactory(new SubjectTypeRegistry(typeProvider), serializer, serviceProvider);

        // Act
        var subject = await factory.CreateFromBlobAsync(
            StorageFactory.Blobs.InMemory(),
            new Mock<IStorageContainer>().Object,
            new Blob("device.loading"),
            CancellationToken.None);

        // Assert
        var file = Assert.IsType<ContentLoadingActivatableFile>(subject);
        Assert.False(file.WasActivatedWhenContentLoaded);
        Assert.Single(subject.GetHostedServiceAttachments());
    }
}

[InterceptorSubject]
[FileExtension(".activatable")]
public partial class ActivatableFile : ISubjectHostedServiceFactory
{
    public ActivatableFile(IStorageContainer storage, string fullPath)
    {
    }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
        => new NoOpHostedService();

    private sealed class NoOpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

[InterceptorSubject]
[FileExtension(".loading")]
public partial class ContentLoadingActivatableFile : ISubjectHostedServiceFactory, IStorageFile
{
    public ContentLoadingActivatableFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;
    }

    public bool? WasActivatedWhenContentLoaded { get; private set; }

    public string Name => Path.GetFileName(FullPath);

    public string FullPath { get; }

    public IStorageContainer Storage { get; }

    public long FileSize { get; set; }

    public DateTime LastModified { get; set; }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());

    public Task WriteAsync(Stream content, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        WasActivatedWhenContentLoaded = !this.GetHostedServiceAttachments().IsEmpty;
        return Task.CompletedTask;
    }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
        => throw new NotSupportedException("Never started: the file is in no hosting graph.");
}
