using HomeBlaze.Storage.Abstractions;
using HomeBlaze.Storage.Abstractions.Attributes;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;

namespace HomeBlaze.Storage.Tests;

/// <summary>
/// A file subject that has a hosted service and records whether it was already activated when its content loaded.
/// </summary>
[InterceptorSubject]
[FileExtension(".activatable")]
public partial class ActivatableFile : ISubjectHostedServiceFactory, IStorageFile
{
    public ActivatableFile(IStorageContainer storage, string fullPath)
    {
        Storage = storage;
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
    }

    public IStorageContainer Storage { get; }

    public string FullPath { get; }

    public string Name { get; }

    public partial long FileSize { get; set; }

    public partial DateTime LastModified { get; set; }

    public bool? WasActivatedWhenContentLoaded { get; private set; }

    public Task<Stream> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());

    public Task WriteAsync(Stream content, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnFileChangedAsync(CancellationToken cancellationToken)
    {
        WasActivatedWhenContentLoaded = !this.GetHostedServiceAttachments().IsEmpty;
        return Task.CompletedTask;
    }

    IHostedService ISubjectHostedServiceFactory.CreateHostedService(IServiceProvider serviceProvider)
        => new NoOpHostedService();

    private sealed class NoOpHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
