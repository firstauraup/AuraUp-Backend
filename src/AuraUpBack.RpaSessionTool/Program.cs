using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Playwright;

var options = SessionToolOptions.Parse(args);
var outputPath = Path.GetFullPath(options.OutputPath, Directory.GetCurrentDirectory());
var userDataDirPath = Path.GetFullPath(options.UserDataDirPath, Directory.GetCurrentDirectory());
var profileArchivePath = Path.GetFullPath(options.ProfileArchivePath, Directory.GetCurrentDirectory());

Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
Directory.CreateDirectory(userDataDirPath);
Directory.CreateDirectory(Path.GetDirectoryName(profileArchivePath) ?? Directory.GetCurrentDirectory());

Console.WriteLine("AuraUpBack Instagram session renewal tool");
Console.WriteLine($"Output file: {outputPath}");
Console.WriteLine($"User data dir: {userDataDirPath}");
Console.WriteLine($"Profile archive: {profileArchivePath}");
Console.WriteLine($"Headless: {options.Headless}");
Console.WriteLine();
Console.WriteLine("This tool opens Instagram in a real browser window.");
Console.WriteLine("Log in manually, finish captcha / SMS / 2FA, then come back here and press Enter.");
Console.WriteLine();

using var playwright = await Playwright.CreateAsync();
await using var context = await playwright.Chromium.LaunchPersistentContextAsync(userDataDirPath, new BrowserTypeLaunchPersistentContextOptions
{
    Headless = options.Headless,
    SlowMo = options.Headless ? 0 : 75,
    ViewportSize = new ViewportSize
    {
        Width = 1440,
        Height = 980
    },
    ChromiumSandbox = false,
    Args =
    [
        "--no-sandbox",
        "--disable-setuid-sandbox",
        "--disable-dev-shm-usage"
    ]
});

var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
await page.GotoAsync("https://www.instagram.com/accounts/login/", new PageGotoOptions
{
    WaitUntil = WaitUntilState.DOMContentLoaded,
    Timeout = 60_000
});

Console.WriteLine("Browser opened at Instagram login.");
Console.WriteLine("When you are fully logged in and can see the account home/profile, press Enter here.");
Console.ReadLine();

await context.StorageStateAsync(new BrowserContextStorageStateOptions
{
    Path = outputPath
});

var cookies = await context.CookiesAsync();
await context.CloseAsync();
CreateProfileArchive(userDataDirPath, profileArchivePath);

Console.WriteLine();
Console.WriteLine($"Session saved to: {outputPath}");
Console.WriteLine($"Profile archive saved to: {profileArchivePath}");
Console.WriteLine($"Cookies stored: {cookies.Count}");

if (options.ShouldUpload)
{
    var backendToken = string.IsNullOrWhiteSpace(options.BackendToken)
        ? await LoginBackendAsync(options)
        : options.BackendToken.Trim();

    await UploadSessionPackageAsync(options, backendToken, outputPath, profileArchivePath);
    Console.WriteLine("Session package uploaded to the backend.");
}
else
{
    Console.WriteLine("No backend upload configured. Use --api-base-url with either --backend-token or --admin-username/--admin-password to upload automatically.");
}

static void CreateProfileArchive(string userDataDirPath, string profileArchivePath)
{
    if (File.Exists(profileArchivePath))
    {
        File.Delete(profileArchivePath);
    }

    var excludedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

    using var archive = ZipFile.Open(profileArchivePath, ZipArchiveMode.Create);
    foreach (var filePath in EnumerateProfileFiles(userDataDirPath, excludedDirectories))
    {
        var relativePath = Path.GetRelativePath(userDataDirPath, filePath);
        try
        {
            archive.CreateEntryFromFile(filePath, relativePath, CompressionLevel.Fastest);
        }
        catch (IOException)
        {
            // Chromium can keep transient profile files locked briefly even after close.
            // Skip them so the session package remains usable.
        }
        catch (UnauthorizedAccessException)
        {
            // Skip volatile files that cannot be copied on this platform.
        }
    }
}

static IEnumerable<string> EnumerateProfileFiles(string rootDirectory, HashSet<string> excludedDirectories)
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
            if (excludedDirectories.Contains(Path.GetFileName(directory)))
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

static async Task<string> LoginBackendAsync(SessionToolOptions options)
{
    if (string.IsNullOrWhiteSpace(options.ApiBaseUrl) ||
        string.IsNullOrWhiteSpace(options.AdminUsername) ||
        string.IsNullOrWhiteSpace(options.AdminPassword))
    {
        throw new InvalidOperationException("Provide --api-base-url with either --backend-token or both --admin-username and --admin-password.");
    }

    using var httpClient = new HttpClient
    {
        BaseAddress = new Uri(NormalizeApiBaseUrl(options.ApiBaseUrl)),
        Timeout = TimeSpan.FromMinutes(10)
    };

    var response = await httpClient.PostAsJsonAsync("/api/auth/login", new
    {
        username = options.AdminUsername,
        password = options.AdminPassword
    });

    var payload = await response.Content.ReadFromJsonAsync<LoginResponse>();
    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(payload?.AccessToken))
    {
        var responseText = payload?.Message ?? await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Backend login failed: {responseText}");
    }

    return payload.AccessToken;
}

static async Task UploadSessionPackageAsync(
    SessionToolOptions options,
    string backendToken,
    string sessionStatePath,
    string profileArchivePath)
{
    using var httpClient = new HttpClient
    {
        BaseAddress = new Uri(NormalizeApiBaseUrl(options.ApiBaseUrl)),
        Timeout = TimeSpan.FromMinutes(10)
    };
    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", backendToken);

    using var form = new MultipartFormDataContent();
    await using var sessionStateStream = File.OpenRead(sessionStatePath);
    await using var profileArchiveStream = File.OpenRead(profileArchivePath);

    var sessionContent = new StreamContent(sessionStateStream);
    sessionContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    form.Add(sessionContent, "sessionState", Path.GetFileName(sessionStatePath));

    var profileContent = new StreamContent(profileArchiveStream);
    profileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
    form.Add(profileContent, "profileArchive", Path.GetFileName(profileArchivePath));

    var response = await httpClient.PostAsync("/api/integrations/instagram/session-package", form);
    if (!response.IsSuccessStatusCode)
    {
        var responseText = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Backend session upload failed: {responseText}");
    }
}

static string NormalizeApiBaseUrl(string apiBaseUrl)
{
    return string.IsNullOrWhiteSpace(apiBaseUrl)
        ? throw new InvalidOperationException("The backend API base URL is required for upload.")
        : apiBaseUrl.Trim().TrimEnd('/');
}

internal sealed class SessionToolOptions
{
    public string OutputPath { get; init; } = "App_Data/instagram-rpa-session.json";
    public string UserDataDirPath { get; init; } = "App_Data/instagram-rpa-profile";
    public string ProfileArchivePath { get; init; } = "App_Data/instagram-rpa-profile.zip";
    public string ApiBaseUrl { get; init; } = string.Empty;
    public string AdminUsername { get; init; } = string.Empty;
    public string AdminPassword { get; init; } = string.Empty;
    public string BackendToken { get; init; } = string.Empty;
    public bool Headless { get; init; }

    public bool ShouldUpload => !string.IsNullOrWhiteSpace(ApiBaseUrl) &&
                                (!string.IsNullOrWhiteSpace(BackendToken) ||
                                 (!string.IsNullOrWhiteSpace(AdminUsername) && !string.IsNullOrWhiteSpace(AdminPassword)));

    public static SessionToolOptions Parse(string[] args)
    {
        string? outputPath = null;
        string? userDataDirPath = null;
        string? profileArchivePath = null;
        string? apiBaseUrl = null;
        string? adminUsername = null;
        string? adminPassword = null;
        string? backendToken = null;
        var headless = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument.Equals("--output", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                outputPath = args[++index];
                continue;
            }

            if (argument.Equals("--user-data-dir", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                userDataDirPath = args[++index];
                continue;
            }

            if (argument.Equals("--profile-archive", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                profileArchivePath = args[++index];
                continue;
            }

            if (argument.Equals("--api-base-url", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                apiBaseUrl = args[++index];
                continue;
            }

            if (argument.Equals("--admin-username", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                adminUsername = args[++index];
                continue;
            }

            if (argument.Equals("--admin-password", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                adminPassword = args[++index];
                continue;
            }

            if (argument.Equals("--backend-token", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                backendToken = args[++index];
                continue;
            }

            if (argument.Equals("--headless", StringComparison.OrdinalIgnoreCase))
            {
                headless = true;
            }
        }

        return new SessionToolOptions
        {
            OutputPath = string.IsNullOrWhiteSpace(outputPath) ? "App_Data/instagram-rpa-session.json" : outputPath,
            UserDataDirPath = string.IsNullOrWhiteSpace(userDataDirPath) ? "App_Data/instagram-rpa-profile" : userDataDirPath,
            ProfileArchivePath = string.IsNullOrWhiteSpace(profileArchivePath) ? "App_Data/instagram-rpa-profile.zip" : profileArchivePath,
            ApiBaseUrl = apiBaseUrl ?? string.Empty,
            AdminUsername = adminUsername ?? string.Empty,
            AdminPassword = adminPassword ?? string.Empty,
            BackendToken = backendToken ?? string.Empty,
            Headless = headless
        };
    }
}

internal sealed class LoginResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
