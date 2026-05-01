using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class ClipTranscribeVideoTranscriptionService(
    IOptions<TranscriptionOptions> options,
    ILogger<ClipTranscribeVideoTranscriptionService> logger) : IVideoTranscriptionService
{
    private const int InitialGenerationWaitSeconds = 8;
    private static readonly Regex MarketingNoiseRegex = new(
        "transcribe tiktok|instagram reels to text|youtube shorts to text|no credit card required|upgrade to pro|start creating for free|simple pricing|explore tools|how it works|built for modern creators|formato corto|formato largo|preguntas frecuentes|iniciar sesión|inicia sesión|short format|long format|faq|log in|sign in",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NavigationNoiseRegex = new(
        "^(?:formato corto|formato largo|preguntas frecuentes|iniciar sesión|inicia sesión|short format|long format|faq|sign in|log in|pricing|home|tools)(?:\\s+(?:formato corto|formato largo|preguntas frecuentes|iniciar sesión|inicia sesión|short format|long format|faq|sign in|log in|pricing|home|tools))*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TranscriptionOptions _options = options.Value;

    public async Task<string> TranscribeAsync(string videoUrl, string caption, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(videoUrl))
        {
            throw new InvalidOperationException("The reel URL is required to generate a transcript.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.RequestTimeoutSeconds)));
        var timeoutToken = timeoutCts.Token;

        var sessionStatePath = await PrepareSessionStateAsync(cancellationToken);
        var hasSessionState = File.Exists(sessionStatePath);
        var hasLoginCredentials = HasLoginCredentials();

        logger.LogInformation(
            "Entrando {CredentialMode} a ClipTranscribe para transcribir {VideoUrl}. Headless: {Headless}. SessionStateConfigured: {SessionStateConfigured}. LoginConfigured: {LoginConfigured}.",
            hasLoginCredentials ? "con credenciales" : "sin credenciales",
            videoUrl,
            _options.Headless,
            hasSessionState,
            hasLoginCredentials);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _options.Headless,
            ChromiumSandbox = false,
            Args =
            [
                "--no-sandbox",
                "--disable-setuid-sandbox",
                "--disable-dev-shm-usage",
                "--disable-gpu"
            ]
        });

        await using var context = await CreateContextAsync(browser, hasSessionState, sessionStatePath);

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(Math.Max(15, _options.RequestTimeoutSeconds) * 1000);

        await NavigateToTranscriptToolAsync(page);

        if (!hasSessionState && hasLoginCredentials)
        {
            await LoginAsync(page, context, sessionStatePath, cancellationToken);
            await NavigateToTranscriptToolAsync(page);
        }

        var retriedAfterLogin = false;
        while (true)
        {
            string transcript;
            try
            {
                transcript = await SubmitTranscriptionAsync(page, videoUrl, timeoutToken);
            }
            catch (ClipTranscribeAuthenticationRequiredException exception) when (!retriedAfterLogin && hasLoginCredentials)
            {
                retriedAfterLogin = true;
                logger.LogInformation(
                    exception,
                    "ClipTranscribe requested authentication for {VideoUrl}. Retrying after login.",
                    videoUrl);

                await LoginAsync(page, context, sessionStatePath, cancellationToken);
                await NavigateToTranscriptToolAsync(page);
                continue;
            }
            catch (ClipTranscribeAuthenticationRequiredException exception)
            {
                logger.LogWarning(
                    exception,
                    "ClipTranscribe requested authentication for {VideoUrl}, but no valid session or login credentials are available. Falling back to Instagram transcript extraction.",
                    videoUrl);

                return await BuildFallbackTranscriptAsync(context, videoUrl, caption, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogWarning(
                    exception,
                    "ClipTranscribe failed for {VideoUrl}. Falling back to Instagram transcript extraction.",
                    videoUrl);

                return await BuildFallbackTranscriptAsync(context, videoUrl, caption, cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    exception,
                    "ClipTranscribe timed out for {VideoUrl}. Falling back to Instagram transcript extraction.",
                    videoUrl);

                return await BuildFallbackTranscriptAsync(context, videoUrl, caption, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                return transcript;
            }

            return await BuildFallbackTranscriptAsync(context, videoUrl, caption, cancellationToken);
        }
    }

    private async Task<string> PrepareSessionStateAsync(CancellationToken cancellationToken)
    {
        var sessionStatePath = ResolveSessionStatePath(_options.ClipTranscribeSessionStatePath);

        if (!string.IsNullOrWhiteSpace(_options.ClipTranscribeSessionStateBase64))
        {
            await TryWriteSessionStateFromBase64Async(sessionStatePath, cancellationToken);
            return sessionStatePath;
        }

        if (!string.IsNullOrWhiteSpace(_options.ClipTranscribeSessionStateJson))
        {
            await TryWriteSessionStateAsync(
                sessionStatePath,
                _options.ClipTranscribeSessionStateJson,
                "environment JSON token",
                cancellationToken);
            return sessionStatePath;
        }

        if (File.Exists(sessionStatePath))
        {
            logger.LogInformation(
                "ClipTranscribe session will be loaded from stored state file {SessionStatePath}.",
                sessionStatePath);
        }
        else
        {
            logger.LogInformation(
                "ClipTranscribe session state file was not found at {SessionStatePath}. The service will use login credentials when configured.",
                sessionStatePath);
        }

        return sessionStatePath;
    }

    private async Task TryWriteSessionStateFromBase64Async(string sessionStatePath, CancellationToken cancellationToken)
    {
        try
        {
            var sessionStateJson = Encoding.UTF8.GetString(Convert.FromBase64String(_options.ClipTranscribeSessionStateBase64));
            await TryWriteSessionStateAsync(sessionStatePath, sessionStateJson, "environment base64 token", cancellationToken);
        }
        catch (FormatException exception)
        {
            logger.LogWarning(
                exception,
                "ClipTranscribe session state base64 value is invalid. Ignoring the environment token and continuing.");
        }
    }

    private async Task TryWriteSessionStateAsync(
        string sessionStatePath,
        string sessionStateJson,
        string source,
        CancellationToken cancellationToken)
    {
        try
        {
            using var sessionStateDocument = JsonDocument.Parse(sessionStateJson);
            if (!sessionStateDocument.RootElement.TryGetProperty("cookies", out _) ||
                !sessionStateDocument.RootElement.TryGetProperty("origins", out _))
            {
                logger.LogWarning(
                    "ClipTranscribe session state from {SessionSource} is missing Playwright cookies/origins fields. Ignoring the environment token and continuing.",
                    source);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(sessionStatePath) ?? AppContext.BaseDirectory);
            await File.WriteAllTextAsync(sessionStatePath, sessionStateJson, cancellationToken);

            logger.LogInformation(
                "ClipTranscribe session loaded by {SessionSource} into {SessionStatePath}.",
                source,
                sessionStatePath);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(
                exception,
                "ClipTranscribe session state JSON is invalid. Ignoring the environment token and continuing.");
        }
    }

    private async Task<IBrowserContext> CreateContextAsync(IBrowser browser, bool hasSessionState, string sessionStatePath)
    {
        var contextOptions = new BrowserNewContextOptions
        {
            Locale = "en-US",
            ViewportSize = new ViewportSize
            {
                Width = 1440,
                Height = 1080
            },
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            BypassCSP = true,
            StrictSelectors = true,
            ServiceWorkers = ServiceWorkerPolicy.Block
        };

        if (hasSessionState)
        {
            contextOptions.StorageStatePath = sessionStatePath;
            logger.LogInformation(
                "ClipTranscribe browser context loading session by token/storage state from {SessionStatePath}.",
                sessionStatePath);
        }

        var context = await browser.NewContextAsync(contextOptions);
        await GrantClipboardPermissionsAsync(context);
        return context;
    }

    private async Task GrantClipboardPermissionsAsync(IBrowserContext context)
    {
        try
        {
            await context.GrantPermissionsAsync(
                ["clipboard-read", "clipboard-write"],
                new BrowserContextGrantPermissionsOptions
                {
                    Origin = BuildOrigin()
                });
        }
        catch (PlaywrightException exception)
        {
            logger.LogWarning(exception, "No se pudieron habilitar permisos de clipboard para ClipTranscribe.");
        }
    }

    private async Task NavigateToTranscriptToolAsync(IPage page)
    {
        var targetUrl = BuildBaseUrl();
        logger.LogInformation("Opening ClipTranscribe transcript tool at {TargetUrl}.", targetUrl);

        await page.GotoAsync(targetUrl, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = Math.Max(15, _options.RequestTimeoutSeconds) * 1000
        });

        await DismissDecorativeUiAsync(page);
    }

    private async Task<string> SubmitTranscriptionAsync(IPage page, string videoUrl, CancellationToken cancellationToken)
    {
        await DismissDecorativeUiAsync(page);

        var input = await ResolveUrlInputAsync(page);
        logger.LogInformation("ClipTranscribe URL input resolved for {VideoUrl}.", videoUrl);

        await input.FillAsync(videoUrl);
        await input.DispatchEventAsync("input");
        await input.DispatchEventAsync("change");
        logger.LogInformation("Pegando link en ClipTranscribe: {VideoUrl}", videoUrl);

        await SubmitAsync(page, input);
        logger.LogInformation(
            "Transcribiendo {VideoUrl}. Esperando {WaitSeconds} segundos antes de copiar el texto.",
            videoUrl,
            InitialGenerationWaitSeconds);
        await Task.Delay(TimeSpan.FromSeconds(InitialGenerationWaitSeconds), cancellationToken);

        return await WaitForTranscriptAsync(page, videoUrl, cancellationToken);
    }

    private async Task LoginAsync(
        IPage page,
        IBrowserContext context,
        string sessionStatePath,
        CancellationToken cancellationToken)
    {
        if (!HasLoginCredentials())
        {
            throw new InvalidOperationException(
                "ClipTranscribe requested authentication but no login credentials are configured. Set Transcription__ClipTranscribeEmail and Transcription__ClipTranscribePassword.");
        }

        var account = MaskAccount(_options.ClipTranscribeEmail);
        logger.LogInformation("ClipTranscribe login started for account {Account}.", account);

        await OpenLoginPageAsync(page);
        await DismissDecorativeUiAsync(page);

        var emailInput = await ResolveEmailInputAsync(page);
        var passwordInput = await ResolvePasswordInputAsync(page);
        logger.LogInformation("ClipTranscribe login form detected for account {Account}.", account);

        await emailInput.FillAsync(_options.ClipTranscribeEmail);
        await emailInput.DispatchEventAsync("input");
        await emailInput.DispatchEventAsync("change");

        await passwordInput.FillAsync(_options.ClipTranscribePassword);
        await passwordInput.DispatchEventAsync("input");
        await passwordInput.DispatchEventAsync("change");

        await SubmitLoginAsync(page, passwordInput);
        logger.LogInformation("ClipTranscribe login submitted for account {Account}; waiting for authenticated session.", account);

        await WaitForLoginCompletionAsync(page, cancellationToken);
        await SaveSessionStateAsync(context, sessionStatePath, "login");

        logger.LogInformation(
            "ClipTranscribe login completed for account {Account}. Session created by login at {SessionStatePath}.",
            account,
            sessionStatePath);
    }

    private async Task OpenLoginPageAsync(IPage page)
    {
        await page.GotoAsync(BuildBaseUrl(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = Math.Max(15, _options.ClipTranscribeLoginTimeoutSeconds) * 1000
        });

        await DismissDecorativeUiAsync(page);

        if (await TryClickLoginEntryAsync(page) && await HasVisibleLoginInputAsync(page))
        {
            return;
        }

        foreach (var path in new[] { "sign-in", "login", "auth/sign-in", "auth/login" })
        {
            var loginUrl = BuildUrl(path);
            logger.LogInformation("Opening ClipTranscribe login page at {LoginUrl}.", loginUrl);

            await page.GotoAsync(loginUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = Math.Max(15, _options.ClipTranscribeLoginTimeoutSeconds) * 1000
            });

            await page.WaitForTimeoutAsync(750);
            await DismissDecorativeUiAsync(page);

            if (await HasVisibleLoginInputAsync(page) || await IsAuthenticationWallAsync(page))
            {
                return;
            }
        }

        throw new InvalidOperationException("ClipTranscribe login page did not show an email or password input.");
    }

    private static async Task<bool> TryClickLoginEntryAsync(IPage page)
    {
        foreach (var label in new[] { "Sign In", "Log In", "Login", "Iniciar sesión", "Acceder" })
        {
            var candidates = new[]
            {
                page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = label }).First,
                page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = label }).First,
                page.GetByText(label, new PageGetByTextOptions { Exact = true }).First
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    await candidate.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 1_500
                    });

                    await candidate.ClickAsync(new LocatorClickOptions
                    {
                        Force = true
                    });

                    await page.WaitForTimeoutAsync(1_000);
                    return true;
                }
                catch (PlaywrightException)
                {
                }
            }
        }

        return false;
    }

    private static async Task<ILocator> ResolveEmailInputAsync(IPage page)
    {
        foreach (var selector in new[]
                 {
                     "input[type='email']",
                     "input[autocomplete='email']",
                     "input[name*='email' i]",
                     "input[placeholder*='email' i]",
                     "input[placeholder*='correo' i]",
                     "input[type='text']",
                     "input"
                 })
        {
            var locator = page.Locator(selector).First;
            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2_000
                });

                return locator;
            }
            catch (PlaywrightException)
            {
            }
        }

        throw new InvalidOperationException("ClipTranscribe login did not show an email input.");
    }

    private static async Task<ILocator> ResolvePasswordInputAsync(IPage page)
    {
        foreach (var selector in new[]
                 {
                     "input[type='password']",
                     "input[autocomplete='current-password']",
                     "input[name*='password' i]",
                     "input[placeholder*='password' i]",
                     "input[placeholder*='contraseña' i]"
                 })
        {
            var locator = page.Locator(selector).First;
            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 2_000
                });

                return locator;
            }
            catch (PlaywrightException)
            {
            }
        }

        throw new InvalidOperationException("ClipTranscribe login did not show a password input.");
    }

    private static async Task SubmitLoginAsync(IPage page, ILocator passwordInput)
    {
        foreach (var label in new[]
                 {
                     "Continue",
                     "Sign In",
                     "Log In",
                     "Login",
                     "Iniciar sesión",
                     "Acceder",
                     "Continuar"
                 })
        {
            var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = label
            });

            try
            {
                await button.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 1_500
                });

                await button.First.ClickAsync(new LocatorClickOptions
                {
                    Force = true
                });
                return;
            }
            catch (PlaywrightException)
            {
            }
        }

        await passwordInput.PressAsync("Enter");
    }

    private async Task WaitForLoginCompletionAsync(IPage page, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(Math.Max(20, _options.ClipTranscribeLoginTimeoutSeconds));
        var consecutiveAuthenticatedSignals = 0;

        while (DateTime.UtcNow - startedAtUtc < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var loginError = await TryReadLoginErrorAsync(page);
            if (!string.IsNullOrWhiteSpace(loginError))
            {
                throw new InvalidOperationException($"ClipTranscribe login failed: {loginError}");
            }

            if (await HasSignedInSignalAsync(page))
            {
                return;
            }

            if (!await HasVisiblePasswordInputAsync(page) && !IsLoginUrl(page.Url))
            {
                consecutiveAuthenticatedSignals++;
                if (consecutiveAuthenticatedSignals >= 2)
                {
                    return;
                }
            }
            else
            {
                consecutiveAuthenticatedSignals = 0;
            }

            await Task.Delay(1_000, cancellationToken);
        }

        throw new InvalidOperationException(
            $"ClipTranscribe login did not complete after {timeout.TotalSeconds:0} seconds.");
    }

    private static async Task<string?> TryReadLoginErrorAsync(IPage page)
    {
        var bodyText = await TryReadBodyTextAsync(page);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return null;
        }

        foreach (var line in bodyText
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length < 6)
            {
                continue;
            }

            if (line.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("incorrect", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("wrong", StringComparison.OrdinalIgnoreCase) ||
                (line.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                 line.Contains("required", StringComparison.OrdinalIgnoreCase)) ||
                line.Contains("verification", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("check your email", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("magic link", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("inválido", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("verificación", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return null;
    }

    private static async Task<bool> HasVisibleLoginInputAsync(IPage page)
    {
        return await HasVisiblePasswordInputAsync(page) ||
               await IsVisibleAsync(page.Locator("input[type='email']").First) ||
               await IsVisibleAsync(page.Locator("input[placeholder*='email' i]").First) ||
               await IsVisibleAsync(page.Locator("input[placeholder*='correo' i]").First);
    }

    private static async Task<bool> HasVisiblePasswordInputAsync(IPage page)
    {
        return await IsVisibleAsync(page.Locator("input[type='password']").First);
    }

    private static async Task<bool> HasSignedInSignalAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                () => {
                  const bodyText = (document.body?.innerText || '').toLowerCase();
                  return bodyText.includes('sign out') ||
                    bodyText.includes('log out') ||
                    bodyText.includes('dashboard') ||
                    bodyText.includes('my account') ||
                    bodyText.includes('account settings') ||
                    bodyText.includes('cerrar sesión') ||
                    bodyText.includes('mi cuenta');
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> IsVisibleAsync(ILocator locator)
    {
        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 500
            });

            return true;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private async Task SaveSessionStateAsync(IBrowserContext context, string sessionStatePath, string source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(sessionStatePath) ?? AppContext.BaseDirectory);
        await context.StorageStateAsync(new BrowserContextStorageStateOptions
        {
            Path = sessionStatePath
        });

        logger.LogInformation(
            "ClipTranscribe session saved by {SessionSource} to {SessionStatePath}.",
            source,
            sessionStatePath);
    }

    private bool HasLoginCredentials()
    {
        return !string.IsNullOrWhiteSpace(_options.ClipTranscribeEmail) &&
               !string.IsNullOrWhiteSpace(_options.ClipTranscribePassword);
    }

    private static bool IsLoginUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("/sign-in", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
               url.Contains("/auth", StringComparison.OrdinalIgnoreCase);
    }

    private string BuildBaseUrl()
    {
        return new Uri(_options.ClipTranscribeBaseUrl).ToString();
    }

    private string BuildOrigin()
    {
        return new Uri(_options.ClipTranscribeBaseUrl).GetLeftPart(UriPartial.Authority);
    }

    private string BuildUrl(string path)
    {
        return new Uri(new Uri(_options.ClipTranscribeBaseUrl), path).ToString();
    }

    private static string ResolveSessionStatePath(string configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "App_Data/cliptranscribe-rpa-session.json"
            : configuredPath.Trim();

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path, AppContext.BaseDirectory);
    }

    private static string MaskAccount(string account)
    {
        var trimmed = account.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "(not configured)";
        }

        var atIndex = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (atIndex > 0)
        {
            var name = trimmed[..atIndex];
            var domain = trimmed[atIndex..];
            return name.Length <= 1
                ? $"***{domain}"
                : $"{name[0]}***{domain}";
        }

        return trimmed.Length <= 2
            ? "***"
            : $"{trimmed[..2]}***";
    }

    private static async Task DismissDecorativeUiAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(
                """
                () => {
                  const closePattern = /(accept|agree|close|dismiss|got it|continue|aceptar|cerrar|entendido|continuar)/i;
                  const candidates = Array.from(document.querySelectorAll('button, [role="button"]'));
                  for (const element of candidates) {
                    const text = (element.textContent || '').trim();
                    if (!text || !closePattern.test(text)) {
                      continue;
                    }

                    const style = window.getComputedStyle(element);
                    if (style.display === 'none' || style.visibility === 'hidden') {
                      continue;
                    }

                    element.click();
                  }
                }
                """);
        }
        catch (PlaywrightException)
        {
        }
    }

    private static async Task<ILocator> ResolveUrlInputAsync(IPage page)
    {
        var selectors = new[]
        {
            "input[type='url']",
            "input[placeholder='Paste a TikTok, YouTube Short, or Reel link…']",
            "input[placeholder*='paste' i]",
            "input[placeholder*='pega' i]",
            "input[placeholder*='url' i]",
            "textarea[placeholder*='paste' i]",
            "textarea[placeholder*='pega' i]",
            "textarea",
            "input"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            try
            {
                await locator.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 5_000
                });

                return locator;
            }
            catch (PlaywrightException)
            {
            }
        }

        throw new InvalidOperationException("ClipTranscribe did not show a URL input field.");
    }

    private static async Task SubmitAsync(IPage page, ILocator input)
    {
        foreach (var label in new[]
                 {
                     "Transcribe",
                     "Generate Transcript",
                     "Get Transcript",
                     "Create Transcript",
                     "Transcribir",
                     "Generar transcripción",
                     "Obtener transcripción"
                 })
        {
            var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = label
            });

            try
            {
                await button.First.WaitForAsync(new LocatorWaitForOptions
                {
                    State = WaitForSelectorState.Visible,
                    Timeout = 1_500
                });

                await button.First.ClickAsync(new LocatorClickOptions
                {
                    Force = true
                });
                return;
            }
            catch (PlaywrightException)
            {
            }
        }

        await input.PressAsync("Enter");
    }

    private async Task<string> WaitForTranscriptAsync(IPage page, string videoUrl, CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTime.UtcNow;
        var timeout = TimeSpan.FromSeconds(Math.Max(30, _options.RequestTimeoutSeconds));
        var generationLogged = false;

        while (DateTime.UtcNow - startedAtUtc < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var copiedTranscript = await TryCopyTranscriptAsync(page, videoUrl);
            if (!string.IsNullOrWhiteSpace(copiedTranscript))
            {
                return copiedTranscript;
            }

            if (await IsStillWorkingAsync(page))
            {
                if (!generationLogged)
                {
                    generationLogged = true;
                    logger.LogInformation("Transcribiendo {VideoUrl}. ClipTranscribe todavía está procesando.", videoUrl);
                }

                await Task.Delay(1_000, cancellationToken);
                continue;
            }

            var transcript = await TryExtractTranscriptAsync(page, videoUrl);
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                logger.LogInformation(
                    "Copiando texto no estuvo disponible; usando texto detectado en pantalla para {VideoUrl}. Texto: {Transcript}",
                    videoUrl,
                    transcript);
                return transcript;
            }

            if (await IsAuthenticationWallAsync(page))
            {
                throw new ClipTranscribeAuthenticationRequiredException(
                    "ClipTranscribe requested authentication before returning a transcript.");
            }

            var pageError = await TryReadProcessingErrorAsync(page);
            if (!string.IsNullOrWhiteSpace(pageError))
            {
                throw new InvalidOperationException(
                    $"ClipTranscribe reported an error while transcribing '{videoUrl}': {pageError}");
            }

            await Task.Delay(1_500, cancellationToken);
        }

        throw new InvalidOperationException(
            $"ClipTranscribe timed out after {timeout.TotalSeconds:0} seconds while transcribing '{videoUrl}'.");
    }

    private async Task<string?> TryCopyTranscriptAsync(IPage page, string videoUrl)
    {
        var copyButton = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Copy"
        }).First;

        try
        {
            await copyButton.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 1_000
            });

            logger.LogInformation("Copiando texto transcrito para {VideoUrl}.", videoUrl);
            await copyButton.ClickAsync(new LocatorClickOptions
            {
                Force = true
            });

            await page.WaitForTimeoutAsync(500);
            var clipboardText = await TryReadClipboardTextAsync(page);
            if (string.IsNullOrWhiteSpace(clipboardText))
            {
                return null;
            }

            var normalized = NormalizeTranscript(clipboardText);
            if (normalized.Length < 40 ||
                MarketingNoiseRegex.IsMatch(normalized) ||
                NavigationNoiseRegex.IsMatch(normalized) ||
                IsLikelyNavigationNoise(normalized))
            {
                return null;
            }

            logger.LogInformation(
                "Texto copiado desde ClipTranscribe para {VideoUrl}. Texto: {Transcript}",
                videoUrl,
                normalized);
            return normalized;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static async Task<string?> TryReadClipboardTextAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string?>("() => navigator.clipboard?.readText?.() ?? null");
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private async Task<string?> TryExtractTranscriptAsync(IPage page, string videoUrl)
    {
        try
        {
            var candidate = await page.EvaluateAsync<string?>(
                """
                (videoUrl) => {
                  const visible = (element) => {
                    if (!element) {
                      return false;
                    }

                    const style = window.getComputedStyle(element);
                    if (style.display === 'none' || style.visibility === 'hidden') {
                      return false;
                    }

                    const rect = element.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                  };

                  const clean = (value) => (value || '')
                    .replace(/\r/g, '')
                    .replace(/[ \t]+\n/g, '\n')
                    .replace(/\n{3,}/g, '\n\n')
                    .replace(/[ \t]{2,}/g, ' ')
                    .trim();

                  const gatherText = (element) => {
                    if (!element) {
                      return '';
                    }

                    if (element instanceof HTMLTextAreaElement || element instanceof HTMLInputElement) {
                      return clean(element.value);
                    }

                    return clean(element.innerText || element.textContent || '');
                  };

                  const scoreElement = (element, text) => {
                    const metadata = `${element.id || ''} ${element.className || ''}`.toLowerCase();
                    let score = 0;

                    if (/(transcript|caption|script|result|output)/i.test(metadata)) {
                      score += 180;
                    }

                    if (text.includes('\n')) {
                      score += 30;
                    }

                    if (text.length > 240) {
                      score += 80;
                    } else if (text.length > 120) {
                      score += 30;
                    }

                    if (/hook:|main point:|visual pacing:/i.test(text)) {
                      score += 40;
                    }

                    if (videoUrl && text.includes(videoUrl)) {
                      score -= 40;
                    }

                    return score;
                  };

                  const transcriptScopedCandidates = [];
                  const preferredNodes = Array.from(document.querySelectorAll('.mt-6.animate-fade-up, [class*="animate-fade-up"], [class*="transcript" i], [id*="transcript" i]'));
                  for (const element of preferredNodes) {
                    if (!visible(element)) {
                      continue;
                    }

                    const text = gatherText(element);
                    if (text.length < 40) {
                      continue;
                    }

                    transcriptScopedCandidates.push({ text, score: scoreElement(element, text) + 400 });
                  }

                  const scopedNodes = Array.from(document.querySelectorAll('[id*="transcript" i], [class*="transcript" i], [data-testid*="transcript" i], [id*="caption" i], [class*="caption" i], textarea, pre'));

                  for (const element of scopedNodes) {
                    if (!visible(element)) {
                      continue;
                    }

                    const text = gatherText(element);
                    if (text.length < 80) {
                      continue;
                    }

                    transcriptScopedCandidates.push({ text, score: scoreElement(element, text) });
                  }

                  if (transcriptScopedCandidates.length) {
                    transcriptScopedCandidates.sort((left, right) => right.score - left.score);
                    return transcriptScopedCandidates[0].text;
                  }

                  const genericCandidates = [];
                  const genericNodes = Array.from(document.querySelectorAll('textarea, pre, article, section, div, p'));
                  for (const element of genericNodes) {
                    if (!visible(element)) {
                      continue;
                    }

                    const text = gatherText(element);
                    if (text.length < 100) {
                      continue;
                    }

                    genericCandidates.push({ text, score: scoreElement(element, text) });
                  }

                  if (!genericCandidates.length) {
                    return null;
                  }

                  genericCandidates.sort((left, right) => right.score - left.score);
                  return genericCandidates[0].text;
                }
                """,
                videoUrl);

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            var normalized = NormalizeTranscript(candidate);
            if (normalized.Length < 40)
            {
                return null;
            }

            if (MarketingNoiseRegex.IsMatch(normalized))
            {
                return null;
            }

            if (NavigationNoiseRegex.IsMatch(normalized) || IsLikelyNavigationNoise(normalized))
            {
                logger.LogInformation("ClipTranscribe rejected navigation text while reading transcript output.");
                return null;
            }

            return normalized;
        }
        catch (PlaywrightException exception)
        {
            logger.LogWarning(exception, "ClipTranscribe DOM extraction failed while reading transcript output.");
            return null;
        }
    }

    private static async Task<bool> IsAuthenticationWallAsync(IPage page)
    {
        try
        {
            var currentUrl = page.Url ?? string.Empty;
            if (currentUrl.Contains("/sign-in", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("/login", StringComparison.OrdinalIgnoreCase) ||
                currentUrl.Contains("/auth", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return await page.EvaluateAsync<bool>(
                """
                () => {
                  const visible = (element) => {
                    if (!element) {
                      return false;
                    }

                    const style = window.getComputedStyle(element);
                    if (style.display === 'none' || style.visibility === 'hidden') {
                      return false;
                    }

                    const rect = element.getBoundingClientRect();
                    return rect.width > 0 && rect.height > 0;
                  };

                  const passwordField = Array.from(document.querySelectorAll('input[type="password"]'))
                    .find((element) => visible(element));
                  if (passwordField) {
                    return true;
                  }

                  const authButtons = Array.from(document.querySelectorAll('button, [role="button"], a'))
                    .filter((element) => visible(element))
                    .map((element) => (element.textContent || '').trim().toLowerCase())
                    .filter(Boolean);

                  const bodyText = (document.body?.innerText || '').toLowerCase();
                  const hasAuthHeading =
                    bodyText.includes('continue with google') ||
                    bodyText.includes('continue with email') ||
                    bodyText.includes('enter your email') ||
                    bodyText.includes('sign in to continue') ||
                    bodyText.includes('log in to continue') ||
                    bodyText.includes('continuar con google') ||
                    bodyText.includes('continuar con correo') ||
                    bodyText.includes('inicia sesión para continuar') ||
                    bodyText.includes('iniciar sesión para continuar');

                  const signInButtons = authButtons.filter((text) =>
                    text === 'sign in' ||
                    text === 'log in' ||
                    text === 'iniciar sesión' ||
                    text === 'acceder');
                  return hasAuthHeading && signInButtons.length > 0;
                }
                """);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> IsStillWorkingAsync(IPage page)
    {
        try
        {
            var bodyText = await TryReadBodyTextAsync(page);
            return bodyText.Contains("Working…", StringComparison.OrdinalIgnoreCase) ||
                   bodyText.Contains("Working...", StringComparison.OrdinalIgnoreCase) ||
                   bodyText.Contains("transcribing", StringComparison.OrdinalIgnoreCase) ||
                   bodyText.Contains("generating transcript", StringComparison.OrdinalIgnoreCase) ||
                   bodyText.Contains("processing", StringComparison.OrdinalIgnoreCase) ||
                   bodyText.Contains("trabajando", StringComparison.OrdinalIgnoreCase) ||
                   bodyText.Contains("transcribiendo", StringComparison.OrdinalIgnoreCase) ||
                   bodyText.Contains("procesando", StringComparison.OrdinalIgnoreCase);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<string?> TryReadProcessingErrorAsync(IPage page)
    {
        try
        {
            var bodyText = await TryReadBodyTextAsync(page);
            if (string.IsNullOrWhiteSpace(bodyText))
            {
                return null;
            }

            foreach (var line in bodyText
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.Length < 8)
                {
                    continue;
                }

                if (line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("unable", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("too many requests", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("no transcript", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("fallo", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("error al", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("intenta de nuevo", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("demasiadas solicitudes", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("sin transcripción", StringComparison.OrdinalIgnoreCase))
                {
                    return line;
                }
            }

            return null;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    private static string NormalizeTranscript(string value)
    {
        var normalized = value
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Trim();

        normalized = Regex.Replace(
            normalized,
            @"^\s*TRANSCRIPT\s*(Timestamps)?\s*Copy\s*",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        normalized = Regex.Replace(
            normalized,
            @"\s*(Analyze Comments|Create My Version).*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        normalized = Regex.Replace(
            normalized,
            @"\s*(Translate|Traducir|Copy|Copiar|Download|Descargar).*$",
            string.Empty,
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        normalized = Regex.Replace(normalized, @"[ \t]{2,}", " ");
        return normalized.Trim(' ', '"', '\'', '“', '”', '‘', '’');
    }

    private static bool IsLikelyNavigationNoise(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalized = value
            .Replace('\n', ' ')
            .Trim();

        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Length == 0)
        {
            return true;
        }

        var noisePhrases = new[]
        {
            "formato corto",
            "formato largo",
            "preguntas frecuentes",
            "iniciar sesión",
            "inicia sesión",
            "short format",
            "long format",
            "faq",
            "sign in",
            "log in",
            "pricing",
            "tools",
            "home"
        };

        var matched = noisePhrases.Count(phrase => normalized.Contains(phrase, StringComparison.OrdinalIgnoreCase));
        if (matched >= 2 && tokens.Length <= 16)
        {
            return true;
        }

        var distinctTokenCount = tokens
            .Select(token => token.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();

        return distinctTokenCount <= 6 && tokens.Length <= 12 && matched > 0;
    }

    private static async Task<string> TryReadBodyTextAsync(IPage page)
    {
        try
        {
            return (await page.Locator("body").InnerTextAsync()).Trim();
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    private async Task<string> BuildFallbackTranscriptAsync(
        IBrowserContext context,
        string videoUrl,
        string caption,
        CancellationToken cancellationToken)
    {
        var instagramFallback = await TryExtractTranscriptFromInstagramAsync(context, videoUrl, cancellationToken);
        if (!string.IsNullOrWhiteSpace(instagramFallback))
        {
            return instagramFallback;
        }

        return BuildFallbackTranscript(videoUrl, caption);
    }

    private async Task<string?> TryExtractTranscriptFromInstagramAsync(
        IBrowserContext context,
        string videoUrl,
        CancellationToken cancellationToken)
    {
        IPage? instagramPage = null;

        try
        {
            instagramPage = await context.NewPageAsync();
            instagramPage.SetDefaultTimeout(Math.Max(15, _options.RequestTimeoutSeconds) * 1000);

            await instagramPage.GotoAsync(videoUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = Math.Max(15, _options.RequestTimeoutSeconds) * 1000
            });

            await instagramPage.WaitForTimeoutAsync(2_000);
            cancellationToken.ThrowIfCancellationRequested();

            var payloadJson = await instagramPage.EvaluateAsync<string>(
                """
                () => JSON.stringify({
                  title: document.querySelector('meta[property="og:title"]')?.getAttribute('content') || '',
                  description: document.querySelector('meta[property="og:description"]')?.getAttribute('content') || '',
                  bodyText: document.body?.innerText || ''
                })
                """);

            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                return null;
            }

            var payload = System.Text.Json.JsonSerializer.Deserialize<InstagramFallbackPayload>(payloadJson);
            if (payload is null)
            {
                return null;
            }

            var extracted = ExtractInstagramTranscriptCandidate(payload.Title, payload.Description, payload.BodyText);
            if (string.IsNullOrWhiteSpace(extracted))
            {
                return null;
            }

            logger.LogInformation("Instagram fallback transcript extracted for {VideoUrl}", videoUrl);
            return extracted;
        }
        catch (PlaywrightException exception)
        {
            logger.LogWarning(exception, "Instagram fallback transcript extraction failed for {VideoUrl}", videoUrl);
            return null;
        }
        finally
        {
            if (instagramPage is not null)
            {
                try
                {
                    await instagramPage.CloseAsync();
                }
                catch (PlaywrightException)
                {
                }
            }
        }
    }

    private static string? ExtractInstagramTranscriptCandidate(string title, string description, string bodyText)
    {
        foreach (var candidate in new[]
                 {
                     NormalizeInstagramCandidate(title),
                     NormalizeInstagramCandidate(description),
                     ExtractInstagramBodyCandidate(bodyText)
                 })
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ExtractInstagramBodyCandidate(string bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            return null;
        }

        var candidates = bodyText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeInstagramCandidate)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .Where(value => value.Length >= 20)
            .OrderByDescending(value => value.Length)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static string? NormalizeInstagramCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        normalized = Regex.Replace(normalized, @"\s+", " ");
        normalized = Regex.Replace(normalized, @"^\s*Watch this reel by .*? on Instagram:\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"^\s*Ver este reel de .*? en Instagram:\s*", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+\d[\d\.,]*\s+(?:likes?|me gusta|comments?|comentarios|views?|visualizaciones)\b.*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = normalized.Trim(' ', '-', '|', ':', '.', ',', '"', '\'', '“', '”', '‘', '’');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.Length < 8)
        {
            return null;
        }

        if (MarketingNoiseRegex.IsMatch(normalized) ||
            NavigationNoiseRegex.IsMatch(normalized) ||
            IsLikelyNavigationNoise(normalized))
        {
            return null;
        }

        var banned = new[]
        {
            "instagram",
            "captured via rpa",
            "capturada mediante rpa",
            "audio original",
            "original audio",
            "iniciar sesión",
            "log in",
            "sign in"
        };

        if (banned.Any(pattern => normalized.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return normalized;
    }

    private static string BuildFallbackTranscript(string videoUrl, string caption)
    {
        var normalizedCaption = NormalizeInstagramCandidate(caption);
        if (!string.IsNullOrWhiteSpace(normalizedCaption))
        {
            return normalizedCaption;
        }

        return "Transcripción no disponible desde proveedor externo.";
    }

    private sealed record InstagramFallbackPayload(
        string Title,
        string Description,
        string BodyText);

    private sealed class ClipTranscribeAuthenticationRequiredException(string message) : InvalidOperationException(message);
}
