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
        "transcribe tiktok|instagram reels to text|youtube shorts to text|no credit card required|upgrade to pro|start creating for free|simple pricing|explore tools|how it works|built for modern creators",
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
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ClipTranscribe timed out while waiting for the transcript for '{videoUrl}'.",
                exception);
        }

        if (!string.IsNullOrWhiteSpace(transcript))
        {
            return transcript;
        }

        throw new InvalidOperationException(
            $"ClipTranscribe did not return a usable transcript for '{videoUrl}'.");
    }

    private static async Task DismissDecorativeUiAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(
                """
                () => {
                  const closePattern = /(accept|agree|close|dismiss|got it|continue)/i;
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
            "textarea[placeholder*='paste' i]",
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
        var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Transcribe",
            Exact = true
        }).First;

        try
        {
            await button.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            });

            await button.ClickAsync(new LocatorClickOptions
            {
                Force = true
            });
            return;
        }
        catch (PlaywrightException)
        {
            await input.PressAsync("Enter");
        }
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
            if (normalized.Length < 80)
            {
                return null;
            }

            if (MarketingNoiseRegex.IsMatch(normalized))
            {
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
                    bodyText.includes('log in to continue');

                  const signInButtons = authButtons.filter((text) => text === 'sign in' || text === 'log in');
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
                   bodyText.Contains("transcribing", StringComparison.OrdinalIgnoreCase);
        }
        catch (PlaywrightException)
        {
            return false;
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

        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        normalized = Regex.Replace(normalized, @"[ \t]{2,}", " ");
        return normalized.Trim();
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
}
