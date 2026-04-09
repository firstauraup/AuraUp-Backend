using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InstagramBrowserProfileService(IOptions<InstagramIntegrationOptions> options)
{
    private readonly InstagramIntegrationOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string SessionStatePath => ResolvePath(_options.RpaSessionStatePath, "App_Data/instagram-rpa-session.json");

    public bool HasPersistentProfile()
    {
        var userDataDirPath = ResolveUserDataDirPath();
        return Directory.Exists(userDataDirPath) && Directory.EnumerateFileSystemEntries(userDataDirPath).Any();
    }

    public void EnsureProfileDirectory()
    {
        Directory.CreateDirectory(ResolveUserDataDirPath());
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
}
