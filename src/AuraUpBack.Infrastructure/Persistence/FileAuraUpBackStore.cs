using System.Text.Json;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Persistence;

internal sealed class FileAuraUpBackStore
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _filePath;

    public FileAuraUpBackStore(IOptions<AuraUpBackStorageOptions> options, IHostEnvironment hostEnvironment)
    {
        var configuredPath = options.Value.DataPath;
        _filePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(hostEnvironment.ContentRootPath, configuredPath);
    }

    public async Task<T> ReadAsync<T>(Func<AuraUpBackSnapshot, T> callback, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var snapshot = await LoadSnapshotAsync(cancellationToken);
            return callback(snapshot);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task WriteAsync(Action<AuraUpBackSnapshot> callback, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var snapshot = await LoadSnapshotAsync(cancellationToken);
            callback(snapshot);
            await PersistSnapshotAsync(snapshot, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AuraUpBackSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        EnsureStorageExists();

        if (!File.Exists(_filePath))
        {
            return new AuraUpBackSnapshot();
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<AuraUpBackSnapshot>(stream, JsonSerializerOptions, cancellationToken)
            ?? new AuraUpBackSnapshot();
    }

    private async Task PersistSnapshotAsync(AuraUpBackSnapshot snapshot, CancellationToken cancellationToken)
    {
        EnsureStorageExists();

        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, JsonSerializerOptions, cancellationToken);
    }

    private void EnsureStorageExists()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
