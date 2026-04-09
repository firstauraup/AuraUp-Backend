using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using System.Text.Json;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InstagramConnectionAutomation(
    IInstagramConnectionRepository instagramConnectionRepository,
    IInstagramCredentialVault credentialVault,
    IOptions<InstagramIntegrationOptions> options,
    InstagramBrowserProfileService browserProfileService,
    IInstagramSettingsService settingsService,
    ILogger<InstagramConnectionAutomation> logger)
    : IInstagramConnectionAutomation
{
    private readonly InstagramIntegrationOptions _options = options.Value;
    private readonly SemaphoreSlim _manualLoginGate = new(1, 1);
    private ManualLoginSession? _manualLoginSession;

    public async Task<InstagramConnectionState> ConnectAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Instagram username and password are required.");
        }

        var nowUtc = DateTime.UtcNow;
        var encryptedPassword = credentialVault.Encrypt(password.Trim());
        var sessionStatePath = ResolveSessionStatePath(_options.RpaSessionStatePath);
        browserProfileService.EnsureProfileDirectory();
        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken)
            ?? InstagramConnection.Create(username, encryptedPassword, sessionStatePath, nowUtc);

        connection.UpdateCredentials(username, encryptedPassword, sessionStatePath, nowUtc);
        await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);

        if (!_options.RpaHeadless)
        {
            return await StartOrResumeManualLoginAsync(connection, password.Trim(), cancellationToken);
        }

        return await LoginAsync(connection, password.Trim(), cancellationToken);
    }

    public async Task<InstagramConnectionState> ReconnectAsync(CancellationToken cancellationToken)
    {
        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken)
            ?? throw new InvalidOperationException("No Instagram connection has been configured yet.");

        if (!connection.HasStoredCredentials)
        {
            throw new InvalidOperationException("The Instagram connection does not have stored credentials.");
        }

        var password = credentialVault.Decrypt(connection.EncryptedPassword);

        if (!_options.RpaHeadless)
        {
            return await StartOrResumeManualLoginAsync(connection, password, cancellationToken);
        }

        return await LoginAsync(connection, password, cancellationToken);
    }

    public async Task<InstagramConnectionState> ImportSessionPackageAsync(
        Stream sessionStateStream,
        Stream? profileArchiveStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionStateStream);

        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken)
            ?? throw new InvalidOperationException("No Instagram connection has been configured yet.");

        await _manualLoginGate.WaitAsync(cancellationToken);

        try
        {
            await DisposeManualLoginSessionAsync();

            await using (await browserProfileService.AcquireExclusiveAccessAsync(cancellationToken))
            {
                if (profileArchiveStream is not null)
                {
                    await browserProfileService.ReplaceProfileFromArchiveAsync(profileArchiveStream, cancellationToken);
                }

                await browserProfileService.WriteSessionStateAsync(sessionStateStream, cancellationToken);
            }

            var sessionStatePath = ResolveSessionStatePath(connection);
            connection.SessionStatePath = sessionStatePath;

            if (await ImportedSessionLooksUsableAsync(sessionStatePath, cancellationToken))
            {
                connection.MarkConnected(DateTime.UtcNow);
            }
            else
            {
                connection.MarkReconnectRequired(
                    "The uploaded Instagram session could not be validated. Run the local renewal flow again after finishing captcha or verification.",
                    DateTime.UtcNow);
            }

            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            return ToState(connection);
        }
        finally
        {
            _manualLoginGate.Release();
        }
    }

    public async Task<InstagramConnectionState> StartManualLoginAsync(CancellationToken cancellationToken)
    {
        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken)
            ?? throw new InvalidOperationException("No Instagram connection has been configured yet.");

        if (!connection.HasStoredCredentials)
        {
            throw new InvalidOperationException("The Instagram connection does not have stored credentials.");
        }

        var password = credentialVault.Decrypt(connection.EncryptedPassword);
        return await StartOrResumeManualLoginAsync(connection, password, cancellationToken);
    }

    public async Task<InstagramConnectionState> CompleteManualLoginAsync(CancellationToken cancellationToken)
    {
        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken)
            ?? throw new InvalidOperationException("No Instagram connection has been configured yet.");

        return await TryCompleteManualLoginAsync(connection, cancellationToken);
    }

    public async Task<InstagramConnectionState> VerifyCodeAsync(string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("The Instagram verification code is required.");
        }

        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken)
            ?? throw new InvalidOperationException("No Instagram connection has been configured yet.");

        if (connection.Status != InstagramConnectionStatus.VerificationRequired)
        {
            throw new InvalidOperationException("The Instagram connection is not waiting for a verification code.");
        }

        var sessionStatePath = ResolveSessionStatePath(connection);
        if (!File.Exists(sessionStatePath) && !browserProfileService.HasPersistentProfile())
        {
            throw new InvalidOperationException("The verification session was not found. Start the connection again.");
        }

        connection.SessionStatePath = sessionStatePath;
        return await CompleteVerificationAsync(connection, code.Trim(), cancellationToken);
    }

    public async Task<InstagramConnectionState> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken);
        if (connection is null)
        {
            return CreateDisconnectedState();
        }

        if (connection.Status == InstagramConnectionStatus.VerificationRequired)
        {
            return ToState(connection);
        }

        var sessionStatePath = ResolveSessionStatePath(connection);
        var sessionExists = File.Exists(sessionStatePath);
        if ((sessionExists || browserProfileService.HasPersistentProfile()) && await SessionLooksValidAsync(sessionStatePath, cancellationToken))
        {
            connection.SessionStatePath = sessionStatePath;
            connection.MarkValidated(DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            return ToState(connection);
        }

        if (!connection.HasStoredCredentials)
        {
            connection.MarkReconnectRequired("Saved session is missing or expired. Credentials are required to log in again.", DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            return ToState(connection);
        }

        if (!_options.RpaHeadless)
        {
            connection.MarkReconnectRequired(
                "Saved Instagram session expired. Start or complete the manual login flow from the admin to refresh it.",
                DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            return ToState(connection);
        }

        var password = credentialVault.Decrypt(connection.EncryptedPassword);

        try
        {
            return await LoginAsync(connection, password, cancellationToken);
        }
        catch (Exception exception) when (CanFallbackToPublicMode(exception))
        {
            connection.MarkReconnectRequired(
                $"Instagram session refresh failed. Public profile mode remains available. Details: {exception.Message}",
                DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            logger.LogWarning(exception,
                "Instagram session refresh failed for @{Username}. Falling back to public profile mode.",
                connection.Username);
            return ToState(connection);
        }
    }

    public async Task<InstagramConnectionState> GetStatusAsync(CancellationToken cancellationToken)
    {
        var connection = await instagramConnectionRepository.GetActiveAsync(cancellationToken);
        return connection is null ? CreateDisconnectedState() : ToState(connection);
    }

    private async Task<InstagramConnectionState> StartOrResumeManualLoginAsync(
        InstagramConnection connection,
        string password,
        CancellationToken cancellationToken)
    {
        await _manualLoginGate.WaitAsync(cancellationToken);

        try
        {
            if (HasReusableManualLoginSession(connection.Id))
            {
                if (await TryCompleteManualLoginCoreAsync(connection, cancellationToken, completeIfAuthenticated: true))
                {
                    return ToState(connection);
                }

                await _manualLoginSession!.Page.BringToFrontAsync();
                connection.MarkVerificationRequired(
                    "Finish the Instagram login in the opened Chromium window, then click Complete manual login.",
                    _manualLoginSession.Page.Url,
                    DateTime.UtcNow);
                await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
                return ToState(connection);
            }

            await DisposeManualLoginSessionAsync();

            var lease = await browserProfileService.AcquireAsync(headless: false, cancellationToken);
            var page = await InstagramBrowserProfileService.GetOrCreatePrimaryPageAsync(lease.Context);
            _manualLoginSession = new ManualLoginSession(connection.Id, lease, page);

            var targetUrl = ResolveManualLoginTargetUrl();
            await OpenManualLoginPageAsync(page, targetUrl);

            await DismissCookiePromptAsync(page);
            await TryPrefillLoginFormAsync(page, connection.Username, password);

            if (await TryCompleteManualLoginCoreAsync(connection, cancellationToken, completeIfAuthenticated: true))
            {
                return ToState(connection);
            }

            connection.MarkVerificationRequired(
                "Manual Instagram login started. Finish the challenge in the opened Chromium window, then click Complete manual login.",
                page.Url,
                DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            return ToState(connection);
        }
        catch
        {
            await DisposeManualLoginSessionAsync();
            throw;
        }
        finally
        {
            _manualLoginGate.Release();
        }
    }

    private async Task<InstagramConnectionState> TryCompleteManualLoginAsync(
        InstagramConnection connection,
        CancellationToken cancellationToken)
    {
        await _manualLoginGate.WaitAsync(cancellationToken);

        try
        {
            if (await TryCompleteManualLoginCoreAsync(connection, cancellationToken, completeIfAuthenticated: true))
            {
                return ToState(connection);
            }

            if (_manualLoginSession is null || _manualLoginSession.ConnectionId != connection.Id)
            {
                throw new InvalidOperationException("No manual Instagram login window is active. Start the manual login first.");
            }

            connection.MarkVerificationRequired(
                "Instagram still needs manual action. Finish the challenge in the opened Chromium window, then click Complete manual login again.",
                SafeGetPageUrl(_manualLoginSession.Page, connection.VerificationUrl),
                DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            return ToState(connection);
        }
        finally
        {
            _manualLoginGate.Release();
        }
    }

    private async Task<bool> TryCompleteManualLoginCoreAsync(
        InstagramConnection connection,
        CancellationToken cancellationToken,
        bool completeIfAuthenticated)
    {
        var session = _manualLoginSession;
        if (session is null || session.ConnectionId != connection.Id)
        {
            return false;
        }

        if (session.Page.IsClosed)
        {
            await DisposeManualLoginSessionAsync();
            return false;
        }

        var page = session.Page;
        await page.BringToFrontAsync();

        var outcome = await ResolveLoginOutcomeAsync(page, cancellationToken);
        if (outcome == InstagramLoginOutcome.Unknown)
        {
            await page.GotoAsync("https://www.instagram.com/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = Math.Max(60, _options.LoginTimeoutSeconds) * 1_000
            });

            await page.WaitForTimeoutAsync(2_000);
            outcome = await ResolveLoginOutcomeAsync(page, cancellationToken);
        }

        if (outcome == InstagramLoginOutcome.Authenticated && completeIfAuthenticated)
        {
            await EnsureAuthenticatedSessionAsync(page, cancellationToken);
            await InstagramBrowserProfileService.ExportSessionStateAsync(session.Context, connection.SessionStatePath);

            connection.MarkConnected(DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            await DisposeManualLoginSessionAsync();
            logger.LogInformation("Instagram manual session completed for @{Username}", connection.Username);
            return true;
        }

        if (outcome == InstagramLoginOutcome.InvalidCredentials)
        {
            connection.MarkFailed("Instagram rejected the current credentials during manual login.", DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            await DisposeManualLoginSessionAsync();
            throw new InvalidOperationException("Instagram rejected the current credentials during manual login.");
        }

        connection.MarkVerificationRequired(
            "Instagram still needs manual action in the opened Chromium window.",
            SafeGetPageUrl(page, connection.VerificationUrl),
            DateTime.UtcNow);
        await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
        return false;
    }

    private bool HasReusableManualLoginSession(Guid connectionId)
    {
        var session = _manualLoginSession;
        if (session is null || session.ConnectionId != connectionId)
        {
            return false;
        }

        return !session.Page.IsClosed;
    }

    private async Task DisposeManualLoginSessionAsync()
    {
        if (_manualLoginSession is null)
        {
            return;
        }

        await _manualLoginSession.DisposeAsync();
        _manualLoginSession = null;
    }

    private async Task<InstagramConnectionState> LoginAsync(InstagramConnection connection, string password, CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        connection.SessionStatePath = ResolveSessionStatePath(connection);
        connection.MarkConnecting(nowUtc);
        await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);

        try
        {
            await using var lease = await browserProfileService.AcquireAsync(_options.RpaHeadless, cancellationToken);
            var context = lease.Context;
            var page = await InstagramBrowserProfileService.GetOrCreatePrimaryPageAsync(context);
            await page.GotoAsync("https://www.instagram.com/accounts/login/", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.LoginTimeoutSeconds * 1_000
            });

            await DismissCookiePromptAsync(page);
            var initialOutcome = await ResolveLoginOutcomeAsync(page, cancellationToken);
            if (initialOutcome == InstagramLoginOutcome.Authenticated)
            {
                await EnsureAuthenticatedSessionAsync(page, cancellationToken);
                await InstagramBrowserProfileService.ExportSessionStateAsync(context, connection.SessionStatePath);

                connection.MarkConnected(DateTime.UtcNow);
                await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
                logger.LogInformation("Instagram session was already authenticated for @{Username}", connection.Username);
                return ToState(connection);
            }

            if (initialOutcome == InstagramLoginOutcome.VerificationRequired)
            {
                return await HandleManualRenewalRequiredAsync(
                    connection,
                    page,
                    context,
                    cancellationToken,
                    "Instagram requires manual verification. Renew the session locally and upload it to the backend.");
            }

            ILocator usernameInput;
            ILocator passwordInput;

            try
            {
                usernameInput = await ResolveUsernameInputAsync(page);
                passwordInput = await ResolvePasswordInputAsync(page);
            }
            catch (InvalidOperationException) when (_options.RpaHeadless)
            {
                return await HandleManualRenewalRequiredAsync(
                    connection,
                    page,
                    context,
                    cancellationToken,
                    "Instagram did not show the standard login form. Renew the session locally and upload it to the backend after captcha or verification.");
            }

            await EnterTextReliablyAsync(usernameInput, connection.Username);
            await EnterTextReliablyAsync(passwordInput, password);

            await page.WaitForTimeoutAsync(Math.Max(750, _options.LoginTypingDelayMs * 3));
            await SubmitLoginAsync(page, passwordInput);
            await page.WaitForTimeoutAsync(6_000);

            var loginOutcome = await ResolveLoginOutcomeAsync(page, cancellationToken);

            if ((loginOutcome == InstagramLoginOutcome.VerificationRequired || loginOutcome == InstagramLoginOutcome.Unknown) &&
                !_options.RpaHeadless)
            {
                logger.LogInformation(
                    "Instagram login for @{Username} requires manual intervention. Waiting up to {TimeoutSeconds} seconds for captcha/code completion.",
                    connection.Username,
                    Math.Max(30, _options.ManualInterventionTimeoutSeconds));

                loginOutcome = await WaitForManualLoginCompletionAsync(page, cancellationToken);
            }

            if (loginOutcome == InstagramLoginOutcome.VerificationRequired)
            {
                return await HandleManualRenewalRequiredAsync(
                    connection,
                    page,
                    context,
                    cancellationToken,
                    _options.RpaHeadless
                        ? "Instagram requires manual verification. Renew the session locally and upload it to the backend."
                        : "Instagram requires manual verification. Complete the captcha or code challenge in the opened browser window, then retry if needed.");
            }

            if (loginOutcome == InstagramLoginOutcome.InvalidCredentials)
            {
                throw new InvalidOperationException("Instagram rejected the login. Check the username/email and password.");
            }

            if (loginOutcome != InstagramLoginOutcome.Authenticated)
            {
                throw new InvalidOperationException(await BuildUnexpectedLoginStateMessageAsync(page));
            }

            await EnsureAuthenticatedSessionAsync(page, cancellationToken);
            await InstagramBrowserProfileService.ExportSessionStateAsync(context, connection.SessionStatePath);

            connection.MarkConnected(DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            logger.LogInformation("Instagram session refreshed for @{Username}", connection.Username);
            return ToState(connection);
        }
        catch (Exception exception)
        {
            connection.MarkFailed(exception.Message, DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            logger.LogWarning(exception, "Instagram connection refresh failed for @{Username}", connection.Username);
            throw;
        }
    }

    private async Task<InstagramConnectionState> CompleteVerificationAsync(
        InstagramConnection connection,
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var lease = await browserProfileService.AcquireAsync(_options.RpaHeadless, cancellationToken);
            var context = lease.Context;
            var page = await InstagramBrowserProfileService.GetOrCreatePrimaryPageAsync(context);
            var targetUrl = string.IsNullOrWhiteSpace(connection.VerificationUrl)
                ? "https://www.instagram.com/"
                : connection.VerificationUrl;

            await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.LoginTimeoutSeconds * 1_000
            });

            await page.WaitForTimeoutAsync(2_000);

            if (!await HasChallengeAsync(page))
            {
                if (await IsAuthenticatedAsync(page))
                {
                    await InstagramBrowserProfileService.ExportSessionStateAsync(context, connection.SessionStatePath);

                    connection.MarkConnected(DateTime.UtcNow);
                    await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
                    return ToState(connection);
                }

                throw new InvalidOperationException("Instagram is no longer waiting for a verification code. Start the connection again.");
            }

            var codeInput = await ResolveVerificationCodeInputAsync(page);
            await codeInput.FillAsync(code);
            await SubmitVerificationCodeAsync(page, codeInput);
            await page.WaitForTimeoutAsync(4_000);

            if (await HasChallengeAsync(page))
            {
                connection.MarkVerificationRequired(
                    "Instagram still needs verification. Check if the code is correct or if another code was sent.",
                    page.Url,
                    DateTime.UtcNow);
                await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
                return ToState(connection);
            }

            if (!await IsAuthenticatedAsync(page))
            {
                connection.MarkFailed("Instagram verification did not finish successfully.", DateTime.UtcNow);
                await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
                throw new InvalidOperationException("Instagram verification did not finish successfully.");
            }

            await InstagramBrowserProfileService.ExportSessionStateAsync(context, connection.SessionStatePath);

            connection.MarkConnected(DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            return ToState(connection);
        }
        catch (Exception exception)
        {
            connection.MarkFailed(exception.Message, DateTime.UtcNow);
            await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
            logger.LogWarning(exception, "Instagram verification failed for @{Username}", connection.Username);
            throw;
        }
    }

    private async Task<bool> SessionLooksValidAsync(string sessionStatePath, CancellationToken cancellationToken)
    {
        await using var lease = await browserProfileService.AcquireAsync(_options.RpaHeadless, cancellationToken);
        var context = lease.Context;
        var page = await InstagramBrowserProfileService.GetOrCreatePrimaryPageAsync(context);
        try
        {
            await EnsureAuthenticatedSessionAsync(page, cancellationToken);
            await InstagramBrowserProfileService.ExportSessionStateAsync(context, sessionStatePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ImportedSessionLooksUsableAsync(string sessionStatePath, CancellationToken cancellationToken)
    {
        if (await SessionLooksValidAsync(sessionStatePath, cancellationToken))
        {
            return true;
        }

        return HasInstagramSessionCookies(sessionStatePath);
    }

    private static bool HasInstagramSessionCookies(string sessionStatePath)
    {
        if (!File.Exists(sessionStatePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(sessionStatePath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("cookies", out var cookiesElement) ||
                cookiesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var hasSessionId = false;
            var hasUserId = false;

            foreach (var cookie in cookiesElement.EnumerateArray())
            {
                var name = cookie.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                var domain = cookie.TryGetProperty("domain", out var domainElement)
                    ? domainElement.GetString() ?? string.Empty
                    : string.Empty;
                var value = cookie.TryGetProperty("value", out var valueElement)
                    ? valueElement.GetString() ?? string.Empty
                    : string.Empty;

                if (!domain.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(name, "sessionid", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    hasSessionId = true;
                }

                if ((string.Equals(name, "ds_user_id", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(name, "csrftoken", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    hasUserId = true;
                }
            }

            return hasSessionId && hasUserId;
        }
        catch
        {
            return false;
        }
    }

    private async Task<InstagramConnectionState> HandleManualRenewalRequiredAsync(
        InstagramConnection connection,
        IPage page,
        IBrowserContext context,
        CancellationToken cancellationToken,
        string message)
    {
        await InstagramBrowserProfileService.ExportSessionStateAsync(context, connection.SessionStatePath);

        if (_options.RpaHeadless)
        {
            connection.MarkReconnectRequired(message, DateTime.UtcNow);
        }
        else
        {
            connection.MarkVerificationRequired(message, page.Url, DateTime.UtcNow);
        }

        await instagramConnectionRepository.UpsertAsync(connection, cancellationToken);
        return ToState(connection);
    }

    private static async Task DismissCookiePromptAsync(IPage page)
    {
        var buttons = new[]
        {
            "Only allow essential cookies",
            "Allow all cookies",
            "Permitir solo cookies esenciales",
            "Permitir todas las cookies"
        };

        foreach (var buttonName in buttons)
        {
            var button = page.GetByRole(AriaRole.Button, new() { Name = buttonName });
            if (await button.CountAsync() > 0)
            {
                await button.First.ClickAsync();
                await page.WaitForTimeoutAsync(500);
                return;
            }
        }
    }

    private static async Task TryPrefillLoginFormAsync(IPage page, string username, string password)
    {
        try
        {
            var usernameInput = await ResolveUsernameInputAsync(page);
            var passwordInput = await ResolvePasswordInputAsync(page);

            if (!string.IsNullOrWhiteSpace(await usernameInput.InputValueAsync()))
            {
                return;
            }

            await usernameInput.FillAsync(username);
            await passwordInput.FillAsync(password);
        }
        catch
        {
            // Manual mode stays usable even when Instagram changes the login form.
        }
    }

    private static async Task<ILocator> ResolveUsernameInputAsync(IPage page)
    {
        var selectors = new[]
        {
            "input[name='username']",
            "input[name='email']",
            "input[autocomplete*='username']",
            "input[aria-label='Phone number, username, or email']",
            "input[aria-label='Phone number, username or email address']",
            "input[type='text']"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                return locator.First;
            }
        }

        throw new InvalidOperationException("Instagram username input was not found.");
    }

    private static async Task<ILocator> ResolvePasswordInputAsync(IPage page)
    {
        var selectors = new[]
        {
            "input[name='password']",
            "input[autocomplete='current-password']",
            "input[aria-label='Password']",
            "input[type='password']"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                return locator.First;
            }
        }

        throw new InvalidOperationException("Instagram password input was not found.");
    }

    private static async Task SubmitLoginAsync(IPage page, ILocator passwordInput)
    {
        var buttonCandidates = new[]
        {
            "button[type='submit']",
            "button:has-text('Log in')",
            "button:has-text('Log In')",
            "button:has-text('Iniciar sesión')",
            "button[aria-label='Log in']",
            "button[aria-label='Log In']",
            "div[role='button']:has-text('Log in')",
            "div[role='button']:has-text('Iniciar sesión')"
        };

        foreach (var selector in buttonCandidates)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                var button = locator.First;
                try
                {
                    await button.WaitForAsync(new LocatorWaitForOptions
                    {
                        State = WaitForSelectorState.Visible,
                        Timeout = 5_000
                    });

                    if (!await button.IsDisabledAsync())
                    {
                        await button.ClickAsync();
                        return;
                    }
                }
                catch
                {
                    // Keep trying the next submit strategy.
                }
            }
        }

        var form = page.Locator("form");
        if (await form.CountAsync() > 0)
        {
            try
            {
                await form.First.EvaluateAsync(
                    """
                    form => {
                      if (typeof form.requestSubmit === "function") {
                        form.requestSubmit();
                        return;
                      }

                      if (typeof form.submit === "function") {
                        form.submit();
                      }
                    }
                    """);
                return;
            }
            catch
            {
                // Fall back to keyboard submit below.
            }
        }

        await passwordInput.PressAsync("Enter");
    }

    private async Task EnterTextReliablyAsync(ILocator input, string expectedValue)
    {
        var typingDelay = Math.Max(25, _options.LoginTypingDelayMs);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await input.ClickAsync();
            await input.PressAsync("Meta+A");
            await input.PressAsync("Backspace");
            await input.FillAsync(string.Empty);
            await input.PressSequentiallyAsync(expectedValue, new LocatorPressSequentiallyOptions
            {
                Delay = typingDelay
            });

            await Task.Delay(Math.Max(250, typingDelay * 2));

            var actualValue = await input.InputValueAsync();
            if (string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
            {
                return;
            }

            await input.FillAsync(expectedValue);
            actualValue = await input.InputValueAsync();
            if (string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
            {
                return;
            }
        }

        var finalValue = await input.InputValueAsync();
        throw new InvalidOperationException(
            $"Instagram input mismatch. Expected '{expectedValue}' but found '{finalValue}'.");
    }

    private static async Task<ILocator> ResolveVerificationCodeInputAsync(IPage page)
    {
        var selectors = new[]
        {
            "input[name='verificationCode']",
            "input[name='security_code']",
            "input[autocomplete='one-time-code']",
            "input[inputmode='numeric']",
            "input[type='tel']",
            "input[type='number']"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                return locator.First;
            }
        }

        throw new InvalidOperationException("Instagram verification code input was not found.");
    }

    private static async Task SubmitVerificationCodeAsync(IPage page, ILocator codeInput)
    {
        var selectors = new[]
        {
            "button[type='submit']",
            "form button",
            "button:has-text('Continue')",
            "button:has-text('Next')",
            "button:has-text('Confirm')",
            "button:has-text('Submit')",
            "button:has-text('Continuar')",
            "button:has-text('Siguiente')",
            "button:has-text('Confirmar')"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                await locator.First.ClickAsync();
                return;
            }
        }

        await codeInput.PressAsync("Enter");
    }

    private static async Task<bool> IsAuthenticatedAsync(IPage page)
    {
        var currentUrl = page.Url;
        if (currentUrl.Contains("/accounts/login", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (currentUrl.Contains("/accounts/onetap", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var knownAuthenticatedLocators = new[]
        {
            "a[href='/']",
            "a[href='/direct/inbox/']",
            "a[href='/explore/']",
            "nav",
            "svg[aria-label='Home']",
            "svg[aria-label='Search']",
            "svg[aria-label='Profile']",
            "svg[aria-label='Messenger']",
            "svg[aria-label='New post']",
            "svg[aria-label='Notifications']"
        };

        foreach (var selector in knownAuthenticatedLocators)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() > 0)
            {
                return true;
            }
        }

        var searchInput = page.Locator("input[placeholder='Search'], input[aria-label='Search input'], input[placeholder='Buscar']");
        if (await searchInput.CountAsync() > 0)
        {
            return true;
        }

        var bodyText = await page.Locator("body").InnerTextAsync();
        var authenticatedTextSignals = new[]
        {
            "Save your login info",
            "Save login info",
            "Guardar información de inicio de sesión",
            "Turn on Notifications",
            "Activar notificaciones",
            "Not Now",
            "Ahora no",
            "Continue in browser",
            "Continuar en el navegador"
        };

        if (authenticatedTextSignals.Any(signal => bodyText.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (bodyText.Contains("Home", StringComparison.OrdinalIgnoreCase) &&
            bodyText.Contains("Search", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static async Task<bool> HasChallengeAsync(IPage page)
    {
        var url = page.Url;
        if (url.Contains("challenge", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("two_factor", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("recaptcha", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("auth_platform", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var bodyText = await page.Locator("body").InnerTextAsync();
        return bodyText.Contains("Enter the code", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Security code", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Código de seguridad", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Enter confirmation code", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Ingresa el código", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Choose a way to confirm it's you", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("Suspicious Login Attempt", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("recaptcha", StringComparison.OrdinalIgnoreCase)
            || bodyText.Contains("captcha", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<InstagramLoginOutcome> DetectLoginOutcomeAsync(IPage page)
    {
        if (await HasChallengeAsync(page))
        {
            return InstagramLoginOutcome.VerificationRequired;
        }

        if (await IsAuthenticatedAsync(page))
        {
            return InstagramLoginOutcome.Authenticated;
        }

        var bodyText = await page.Locator("body").InnerTextAsync();
        var invalidCredentialSignals = new[]
        {
            "The password you entered is incorrect",
            "Sorry, your password was incorrect",
            "The username you entered doesn't belong to an account",
            "Incorrect password",
            "Tu contraseña es incorrecta",
            "El nombre de usuario que ingresaste no pertenece a ninguna cuenta"
        };

        if (invalidCredentialSignals.Any(signal => bodyText.Contains(signal, StringComparison.OrdinalIgnoreCase)))
        {
            return InstagramLoginOutcome.InvalidCredentials;
        }

        return InstagramLoginOutcome.Unknown;
    }

    private static async Task<InstagramLoginOutcome> ResolveLoginOutcomeAsync(IPage page, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await DetectLoginOutcomeAsync(page);
            if (outcome != InstagramLoginOutcome.Unknown)
            {
                return outcome;
            }

            var dismissedPrompt = await TryDismissPostLoginPromptsAsync(page);
            await page.WaitForTimeoutAsync(dismissedPrompt ? 1_000 : 1_500);
        }

        return await DetectLoginOutcomeAsync(page);
    }

    private async Task<InstagramLoginOutcome> WaitForManualLoginCompletionAsync(IPage page, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(30, _options.ManualInterventionTimeoutSeconds));
        var deadlineUtc = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await DetectLoginOutcomeAsync(page);
            if (outcome == InstagramLoginOutcome.Authenticated)
            {
                return outcome;
            }

            await TryDismissPostLoginPromptsAsync(page);
            await page.WaitForTimeoutAsync(2_000);
        }

        return await DetectLoginOutcomeAsync(page);
    }

    private static async Task<bool> TryDismissPostLoginPromptsAsync(IPage page)
    {
        var selectors = new[]
        {
            "button:has-text('Not Now')",
            "button:has-text('Not now')",
            "button:has-text('Ahora no')",
            "div[role='button']:has-text('Not Now')",
            "div[role='button']:has-text('Ahora no')",
            "button:has-text('Continue in browser')",
            "button:has-text('Continuar en el navegador')"
        };

        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector);
            if (await locator.CountAsync() == 0)
            {
                continue;
            }

            try
            {
                await locator.First.ClickAsync();
                return true;
            }
            catch
            {
                // Ignore transient overlays and keep evaluating other prompts.
            }
        }

        return false;
    }

    private static async Task<string> BuildUnexpectedLoginStateMessageAsync(IPage page)
    {
        var title = await page.TitleAsync();
        var bodyText = await page.Locator("body").InnerTextAsync();
        var compactBody = string.Join(" ", bodyText
            .Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim();

        if (compactBody.Length > 220)
        {
            compactBody = $"{compactBody[..220]}...";
        }

        return $"Instagram login did not complete. URL: {page.Url}. Title: {title}. Visible text: {compactBody}";
    }

    private async Task EnsureAuthenticatedSessionAsync(IPage page, CancellationToken cancellationToken)
    {
        var candidateUrls = new[]
        {
            "https://www.instagram.com/",
            "https://www.instagram.com/accounts/onetap/"
        };

        foreach (var candidateUrl in candidateUrls)
        {
            try
            {
                await page.GotoAsync(candidateUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = _options.LoginTimeoutSeconds * 1_000
                });
            }
            catch (PlaywrightException)
            {
                // Instagram sometimes aborts intermediate navigations even when the session is already valid.
            }

            await page.WaitForTimeoutAsync(2_000);
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await ResolveLoginOutcomeAsync(page, cancellationToken);
            if (outcome == InstagramLoginOutcome.Authenticated)
            {
                return;
            }

            if (outcome == InstagramLoginOutcome.VerificationRequired)
            {
                throw new InvalidOperationException("Instagram still requires manual verification before the session can be used.");
            }
        }

        throw new InvalidOperationException(await BuildUnexpectedLoginStateMessageAsync(page));
    }

    private InstagramConnectionState ToState(InstagramConnection connection)
    {
        var settings = settingsService.Current;
        var provider = string.IsNullOrWhiteSpace(settings.Provider) ? _options.Provider : settings.Provider;
        var sessionStatePath = ResolveSessionStatePath(connection);
        var sessionExists = File.Exists(sessionStatePath) || browserProfileService.HasPersistentProfile();

        return new InstagramConnectionState(
            provider,
            connection.Username,
            connection.Status,
            connection.HasStoredCredentials,
            sessionStatePath,
            sessionExists,
            connection.VerificationUrl,
            connection.LastLoginAtUtc,
            connection.LastValidatedAtUtc,
            connection.LastError,
            _options.RpaHeadless,
            _options.RpaMaxPosts,
            _options.AllowPublicProfileReadWithoutSession);
    }

    private InstagramConnectionState CreateDisconnectedState()
    {
        var settings = settingsService.Current;
        var provider = string.IsNullOrWhiteSpace(settings.Provider) ? _options.Provider : settings.Provider;
        var sessionStatePath = ResolveSessionStatePath(_options.RpaSessionStatePath);

        return new InstagramConnectionState(
            provider,
            string.Empty,
            InstagramConnectionStatus.Disconnected,
            false,
            sessionStatePath,
            File.Exists(sessionStatePath) || browserProfileService.HasPersistentProfile(),
            string.Empty,
            null,
            null,
            string.Empty,
            _options.RpaHeadless,
            _options.RpaMaxPosts,
            _options.AllowPublicProfileReadWithoutSession);
    }

    private string ResolveSessionStatePath(InstagramConnection connection)
    {
        var configuredPath = ResolveSessionStatePath(_options.RpaSessionStatePath);

        if (!string.IsNullOrWhiteSpace(connection.SessionStatePath) && File.Exists(connection.SessionStatePath))
        {
            return connection.SessionStatePath;
        }

        return configuredPath;
    }

    private static string ResolveSessionStatePath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(configuredPath, AppContext.BaseDirectory);
    }

    private async Task OpenManualLoginPageAsync(IPage page, string targetUrl)
    {
        try
        {
            await page.GotoAsync(targetUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = Math.Max(60, _options.LoginTimeoutSeconds) * 1_000
            });
            return;
        }
        catch (PlaywrightException exception) when (IsManualNavigationAbort(exception, page))
        {
            logger.LogWarning(
                exception,
                "Instagram manual browser navigation was interrupted, but the page is still open. Continuing with the current page state.");

            if (!page.IsClosed)
            {
                await page.WaitForTimeoutAsync(1_500);
                return;
            }

            throw;
        }
    }

    private static string ResolveManualLoginTargetUrl()
    {
        return "https://www.instagram.com/accounts/login/";
    }

    private static bool IsManualNavigationAbort(PlaywrightException exception, IPage page)
    {
        if (page.IsClosed)
        {
            return false;
        }

        var message = exception.Message;
        if (!message.Contains("ERR_ABORTED", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("frame was detached", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("maybe frame was detached", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string SafeGetPageUrl(IPage page, string fallback)
    {
        try
        {
            return string.IsNullOrWhiteSpace(page.Url) ? fallback : page.Url;
        }
        catch
        {
            return fallback;
        }
    }

    private sealed class ManualLoginSession(
        Guid connectionId,
        InstagramBrowserProfileService.PersistentBrowserLease lease,
        IPage page)
        : IAsyncDisposable
    {
        public Guid ConnectionId { get; } = connectionId;
        public IBrowserContext Context { get; } = lease.Context;
        public IPage Page { get; } = page;

        public async ValueTask DisposeAsync()
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private enum InstagramLoginOutcome
    {
        Unknown = 0,
        Authenticated = 1,
        VerificationRequired = 2,
        InvalidCredentials = 3
    }

    private bool CanFallbackToPublicMode(Exception exception)
    {
        if (!_options.AllowPublicProfileReadWithoutSession)
        {
            return false;
        }

        return exception is TimeoutException or PlaywrightException;
    }
}
