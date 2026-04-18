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
    private const string ReelsTranscriptPath = "instagram-reels-transcript-generator";
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

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
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
        });

        await context.AddInitScriptAsync(
            """
            () => {
              try {
                window.localStorage?.clear();
              } catch {}

              try {
                window.sessionStorage?.clear();
              } catch {}

              try {
                if ('caches' in window) {
                  caches.keys().then((keys) => Promise.all(keys.map((key) => caches.delete(key))));
                }
              } catch {}
            }
            """);

        await context.ClearCookiesAsync();

        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(Math.Max(15, _options.RequestTimeoutSeconds) * 1000);

        logger.LogInformation("Starting ClipTranscribe transcription for {VideoUrl}", videoUrl);

        await page.GotoAsync(BuildReelsTranscriptUrl(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = Math.Max(15, _options.RequestTimeoutSeconds) * 1000
        });

        await DismissDecorativeUiAsync(page);

        var input = await ResolveUrlInputAsync(page);
        await input.FillAsync(videoUrl);
        await input.DispatchEventAsync("input");
        await input.DispatchEventAsync("change");
        await SubmitAsync(page, input);

        string transcript;
        try
        {
            transcript = await WaitForTranscriptAsync(page, videoUrl, timeoutToken);
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

        while (DateTime.UtcNow - startedAtUtc < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var transcript = await TryExtractTranscriptAsync(page, videoUrl);
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                logger.LogInformation("ClipTranscribe produced transcript for {VideoUrl}", videoUrl);
                return transcript;
            }

            if (await IsStillWorkingAsync(page))
            {
                await Task.Delay(1_000, cancellationToken);
                continue;
            }

            if (await IsAuthenticationWallAsync(page))
            {
                throw new InvalidOperationException("ClipTranscribe requested authentication before returning a transcript.");
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

    private string BuildReelsTranscriptUrl()
    {
        var baseUri = new Uri(_options.ClipTranscribeBaseUrl);
        return new Uri(baseUri, ReelsTranscriptPath).ToString();
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
}
