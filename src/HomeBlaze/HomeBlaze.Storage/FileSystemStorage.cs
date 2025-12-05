using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.Core.Services;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Storage;

/// <summary>
/// Storage container backed by the local file system.
/// Uses FileSystemWatcher for real-time change detection.
/// </summary>
[InterceptorSubject]
public partial class FileSystemStorage : StorageContainer
{
    // MudBlazor Icons.Material.Filled.FolderOpen
    private const string FileSystemIcon = "<svg style=\"width:24px;height:24px\" viewBox=\"0 0 24 24\"><path fill=\"currentColor\" d=\"M19,20H4C2.89,20 2,19.1 2,18V6C2,4.89 2.89,4 4,4H10L12,6H19A2,2 0 0,1 21,8H21L4,8V18L6.14,10H23.21L20.93,18.5C20.7,19.37 19.92,20 19,20Z\" /></svg>";

    public override string? Icon => FileSystemIcon;

    private FileSystemWatcher? _watcher;
    private readonly SubjectTypeRegistry _typeRegistry;
    private readonly SubjectSerializer _serializer;
    private readonly ILogger<FileSystemStorage>? _logger;

    /// <summary>
    /// Root path of the storage.
    /// </summary>
    [Configuration]
    public partial string Path { get; set; }

    public FileSystemStorage(
        SubjectTypeRegistry typeRegistry,
        SubjectSerializer serializer,
        ILogger<FileSystemStorage>? logger = null) : base(logger)
    {
        _typeRegistry = typeRegistry;
        _serializer = serializer;
        _logger = logger;
        Path = string.Empty;
    }

    protected override async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new InvalidOperationException("Path is not configured");

        var fullPath = System.IO.Path.GetFullPath(Path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        _logger?.LogInformation("Scanning directory: {Path}", fullPath);

        await ScanDirectoryAsync(fullPath, cancellationToken);
    }

    private async Task ScanDirectoryAsync(string directoryPath, CancellationToken cancellationToken)
    {
        var context = ((IInterceptorSubject)this).Context;
        var existingKeys = new HashSet<string>(Children.Keys);

        // Scan files
        foreach (var filePath in Directory.GetFiles(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = System.IO.Path.GetFileName(filePath);

            existingKeys.Remove(fileName);

            if (Children.ContainsKey(fileName))
                continue; // Already exists

            var subject = await CreateSubjectForFileAsync(filePath, context, cancellationToken);
            if (subject != null)
            {
                AddChild(fileName, subject);
                _logger?.LogDebug("Added file: {FileName}", fileName);
            }
        }

        // Scan directories
        foreach (var subDirPath in Directory.GetDirectories(directoryPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dirName = System.IO.Path.GetFileName(subDirPath);
            existingKeys.Remove(dirName);

            if (Children.ContainsKey(dirName))
                continue; // Already exists

            var folder = new Folder(context, _typeRegistry, _serializer, _logger)
            {
                Path = subDirPath,
                Name = dirName
            };

            AddChild(dirName, folder);
            _logger?.LogDebug("Added folder: {DirName}", dirName);
        }

        // Remove items that no longer exist
        foreach (var key in existingKeys)
        {
            RemoveChild(key);
            _logger?.LogDebug("Removed: {Key}", key);
        }
    }

    private async Task<IInterceptorSubject?> CreateSubjectForFileAsync(
        string filePath,
        IInterceptorSubjectContext context,
        CancellationToken cancellationToken)
    {
        var extension = System.IO.Path.GetExtension(filePath).ToLowerInvariant();

        // JSON files - deserialize with Type discriminator
        if (extension == ".json")
        {
            try
            {
                var json = await File.ReadAllTextAsync(filePath, cancellationToken);
                var subject = _serializer.Deserialize(json, context);
                return subject;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to deserialize JSON file: {FilePath}", filePath);
                return null;
            }
        }

        // Check for registered extension mapping
        var mappedType = _typeRegistry.ResolveTypeForExtension(extension);
        if (mappedType != null)
        {
            var subject = CreateFileSubject(mappedType, filePath, context);
            return subject;
        }

        // Default to GenericFile
        return CreateFileSubject(typeof(GenericFile), filePath, context);
    }

    private IInterceptorSubject? CreateFileSubject(Type type, string filePath, IInterceptorSubjectContext context)
    {
        try
        {
            var ctor = type.GetConstructor(new[] { typeof(IInterceptorSubjectContext) });
            if (ctor != null)
            {
                var subject = (IInterceptorSubject)ctor.Invoke(new object[] { context });

                // Set common file properties if available
                SetFileProperties(subject, filePath);

                return subject;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create subject for file: {FilePath}", filePath);
        }

        return null;
    }

    private void SetFileProperties(IInterceptorSubject subject, string filePath)
    {
        var type = subject.GetType();
        var fileInfo = new FileInfo(filePath);

        SetPropertyIfExists(type, subject, "FilePath", filePath);
        SetPropertyIfExists(type, subject, "FileName", fileInfo.Name);
        SetPropertyIfExists(type, subject, "Extension", fileInfo.Extension);
        SetPropertyIfExists(type, subject, "FileSize", fileInfo.Length);
        SetPropertyIfExists(type, subject, "LastModified", fileInfo.LastWriteTimeUtc);
    }

    private static void SetPropertyIfExists(Type type, object target, string propertyName, object value)
    {
        var prop = type.GetProperty(propertyName);
        if (prop != null && prop.CanWrite)
        {
            try
            {
                prop.SetValue(target, value);
            }
            catch
            {
                // Ignore if can't set
            }
        }
    }

    protected override async Task WatchAsync(CancellationToken cancellationToken)
    {
        var fullPath = System.IO.Path.GetFullPath(Path);

        _watcher = new FileSystemWatcher(fullPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFileSystemChange;
        _watcher.Deleted += OnFileSystemChange;
        _watcher.Changed += OnFileSystemChange;
        _watcher.Renamed += OnFileSystemRenamed;
        _watcher.Error += OnFileSystemError;

        _logger?.LogInformation("Watching directory: {Path}", fullPath);

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        finally
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void OnFileSystemChange(object sender, FileSystemEventArgs e)
    {
        _logger?.LogDebug("File system change: {ChangeType} {Name}", e.ChangeType, e.Name);
        // Trigger rescan - could be optimized to only handle the specific change
        Task.Run(async () =>
        {
            try
            {
                await ScanDirectoryAsync(System.IO.Path.GetFullPath(Path), CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error handling file system change");
            }
        });
    }

    private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        _logger?.LogDebug("File system renamed: {OldName} -> {Name}", e.OldName, e.Name);
        OnFileSystemChange(sender, e);
    }

    private void OnFileSystemError(object sender, ErrorEventArgs e)
    {
        _logger?.LogError(e.GetException(), "FileSystemWatcher error");
    }
}
