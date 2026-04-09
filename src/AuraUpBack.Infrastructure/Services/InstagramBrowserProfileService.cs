using System.IO.Compression;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InstagramBrowserProfileService(IOptions<InstagramIntegrationOptions> options)
{
    private static readonly HashSet<string> ExcludedProfileDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache",
        "Code Cache",
        "GPUCache",
        "GrShaderCache",
        "DawnCache",
        "Crashpad",
        "BrowserMetrics",
        "ShaderCache"
    };

    private readonly InstagramIntegrationOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string SessionStatePath => ResolvePath(_options.RpaSessionStatePath, "App_Data/instagram-rpa-session.json");
    public string UserDataDirPath => ResolveUserDataDirPath();

    public bool HasPersistentProfile()
    {
        var userDataDirPath = ResolveUserDataDirPath();
        return Directory.Exists(userDataDirPath) && Directory.EnumerateFileSystemEntries(userDataDirPath).Any();
    }

    public void EnsureProfileDirectory()
    {
        Directory.CreateDirectory(ResolveUserDataDirPath());
    }

    public async Task WriteSessionStateAsync(Stream sessionStateStream, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionStatePath) ?? AppContext.BaseDirectory);

        await using var fileStream = new FileStream(
            SessionStatePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);

        await sessionStateStream.CopyToAsync(fileStream, cancellationToken);
    }

    public async Task ReplaceProfileFromArchiveAsync(Stream archiveStream, CancellationToken cancellationToken)
    {
        var targetDirectory = ResolveUserDataDirPath();
        var tempZipPath = Path.Combine(Path.GetTempPath(), $"instagram-profile-{Guid.NewGuid():N}.zip");
        var tempExtractDirectory = Path.Combine(Path.GetTempPath(), $"instagram-profile-{Guid.NewGuid():N}");

        try
        {
            await using (var zipFileStream = new FileStream(
                             tempZipPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                await archiveStream.CopyToAsync(zipFileStream, cancellationToken);
            }

            Directory.CreateDirectory(tempExtractDirectory);
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractDirectory, overwriteFiles: true);

            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }

            Directory.CreateDirectory(targetDirectory);
            CopyDirectory(tempExtractDirectory, targetDirectory);
        }
        finally
        {
            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
            }

            if (Directory.Exists(tempExtractDirectory))
            {
                Directory.Delete(tempExtractDirectory, recursive: true);
            }
        }
    }

    public async Task CreateProfileArchiveAsync(Stream outputStream, CancellationToken cancellationToken)
    {
        EnsureProfileDirectory();

        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var filePath in EnumerateProfileFiles(UserDataDirPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(UserDataDirPath, filePath);
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Fastest);
            await using var entryStream = entry.Open();
            await using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await fileStream.CopyToAsync(entryStream, cancellationToken);
        }
    }

    public async Task<PersistentBrowserLease> AcquireAsync(bool headless, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var playwright = await Playwright.CreateAsync();
            var context = await playwright.Chromium.LaunchPersistentContextAsync(
                ResolveUserDataDirPath(),
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = headless,
                    SlowMo = headless ? 0 : 75,
                    ChromiumSandbox = false,
                    ViewportSize = new ViewportSize
                    {
                        Width = 1440,
                        Height = 980
                    },
                    Args =
                    [
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-background-networking",
                        "--disable-background-timer-throttling",
                        "--disable-renderer-backgrounding",
                        "--disable-backgrounding-occluded-windows"
                    ]
                });

            return new PersistentBrowserLease(_gate, playwright, context);
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    public async Task<ExclusiveProfileAccessLease> AcquireExclusiveAccessAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        return new ExclusiveProfileAccessLease(_gate);
    }

    public static async Task<IPage> GetOrCreatePrimaryPageAsync(IBrowserContext context)
    {
        var page = context.Pages.FirstOrDefault(page => !page.IsClosed);
        return page ?? await context.NewPageAsync();
    }

    public static async Task ExportSessionStateAsync(IBrowserContext context, string sessionStatePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sessionStatePath) ?? AppContext.BaseDirectory);
        await context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = sessionStatePath
        });
    }

    private string ResolveUserDataDirPath()
    {
        return ResolvePath(_options.RpaUserDataDirPath, "App_Data/instagram-rpa-profile");
    }

    private static IEnumerable<string> EnumerateProfileFiles(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            yield break;
        }

        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(rootDirectory);

        while (pendingDirectories.Count > 0)
        {
            var currentDirectory = pendingDirectories.Pop();

            foreach (var directory in Directory.EnumerateDirectories(currentDirectory))
            {
                var name = Path.GetFileName(directory);
                if (ExcludedProfileDirectories.Contains(name))
                {
                    continue;
                }

                pendingDirectories.Push(directory);
            }

            foreach (var filePath in Directory.EnumerateFiles(currentDirectory))
            {
                yield return filePath;
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(targetDirectory, relativePath));
        }

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetDirectory);
            File.Copy(filePath, targetPath, overwrite: true);
        }
    }

    private static string ResolvePath(string configuredPath, string fallbackRelativePath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath) ? fallbackRelativePath : configuredPath;
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    internal sealed class PersistentBrowserLease(
        SemaphoreSlim gate,
        IPlaywright playwright,
        IBrowserContext context)
        : IAsyncDisposable
    {
        public IBrowserContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Context.CloseAsync().ConfigureAwait(false);
            }
            finally
            {
                playwright.Dispose();
                gate.Release();
            }
        }
    }

    internal sealed class ExclusiveProfileAccessLease(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
