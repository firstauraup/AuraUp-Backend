using Microsoft.Playwright;

var options = SessionToolOptions.Parse(args);
var outputPath = Path.GetFullPath(options.OutputPath, Directory.GetCurrentDirectory());

Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());

Console.WriteLine("AuraUpBack Instagram session tool");
Console.WriteLine($"Output file: {outputPath}");
Console.WriteLine($"Headless: {options.Headless}");
Console.WriteLine();
Console.WriteLine("This tool will open Instagram in a real browser window.");
Console.WriteLine("Log in manually, finish any checkpoint or 2FA, then come back here and press Enter.");
Console.WriteLine();

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
{
    Headless = options.Headless,
    SlowMo = options.Headless ? 0 : 75
});

await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize
    {
        Width = 1440,
        Height = 980
    }
});

var page = await context.NewPageAsync();
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
Console.WriteLine();
Console.WriteLine($"Session saved to: {outputPath}");
Console.WriteLine($"Cookies stored: {cookies.Count}");
Console.WriteLine("Next step: set Instagram__Provider=Rpa and use this file as Instagram__RpaSessionStatePath.");

internal sealed class SessionToolOptions
{
    public string OutputPath { get; init; } = "App_Data/instagram-rpa-session.json";

    public bool Headless { get; init; }

    public static SessionToolOptions Parse(string[] args)
    {
        string? outputPath = null;
        bool headless = false;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];

            if (argument.Equals("--output", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
            {
                outputPath = args[++index];
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
            Headless = headless
        };
    }
}
