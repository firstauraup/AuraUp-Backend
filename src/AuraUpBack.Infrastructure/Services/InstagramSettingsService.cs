using System.Text.Json;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InstagramSettingsService : IInstagramSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;
    private readonly IInstagramCredentialVault _credentialVault;
    private readonly InstagramIntegrationOptions _defaults;
    private InstagramRuntimeSettings _current;

    public InstagramSettingsService(
        IOptions<InstagramIntegrationOptions> integrationOptions,
        IOptions<AuraUpBackStorageOptions> storageOptions,
        IHostEnvironment hostEnvironment,
        IInstagramCredentialVault credentialVault)
    {
        _credentialVault = credentialVault;
        _defaults = integrationOptions.Value;
        _settingsPath = ResolveSettingsPath(storageOptions.Value.InstagramSettingsPath, hostEnvironment.ContentRootPath);
        _current = LoadSettings();
    }

    public InstagramRuntimeSettings Current => _current;

    public Task<InstagramSettingsView> GetViewAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToView(_current));
    }

    public async Task<InstagramSettingsView> UpdateAsync(InstagramSettingsUpdate update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var next = new InstagramRuntimeSettings(
                Provider: NormalizeProvider(update.Provider, _current.Provider),
                ApifyBaseUrl: NormalizeText(update.ApifyBaseUrl, _current.ApifyBaseUrl),
                ApifyActorId: NormalizeText(update.ApifyActorId, _current.ApifyActorId),
                ApifyApiToken: ResolveApiToken(update),
                ApifyRequestTimeoutSeconds: update.ApifyRequestTimeoutSeconds > 0
                    ? update.ApifyRequestTimeoutSeconds
                    : _current.ApifyRequestTimeoutSeconds);

            Persist(next);
            _current = next;
            return ToView(next);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string ResolveApiToken(InstagramSettingsUpdate update)
    {
        if (update.ClearApifyApiToken)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(update.ApifyApiToken))
        {
            return update.ApifyApiToken.Trim();
        }

        return _current.ApifyApiToken;
    }

    private InstagramRuntimeSettings LoadSettings()
    {
        var defaults = new InstagramRuntimeSettings(
            Provider: NormalizeProvider(_defaults.Provider, "Mock"),
            ApifyBaseUrl: NormalizeText(_defaults.ApifyBaseUrl, "https://api.apify.com/v2"),
            ApifyActorId: NormalizeText(_defaults.ApifyActorId, "apify~instagram-scraper"),
            ApifyApiToken: NormalizeText(_defaults.ApifyApiToken, string.Empty),
            ApifyRequestTimeoutSeconds: _defaults.ApifyRequestTimeoutSeconds > 0 ? _defaults.ApifyRequestTimeoutSeconds : 180);

        if (!File.Exists(_settingsPath))
        {
            return defaults;
        }

        var json = File.ReadAllText(_settingsPath);
        var persisted = JsonSerializer.Deserialize<InstagramSettingsDocument>(json, JsonOptions);
        if (persisted is null)
        {
            return defaults;
        }

        var decryptedToken = string.IsNullOrWhiteSpace(persisted.EncryptedApifyApiToken)
            ? defaults.ApifyApiToken
            : _credentialVault.Decrypt(persisted.EncryptedApifyApiToken);

        return new InstagramRuntimeSettings(
            Provider: NormalizeProvider(persisted.Provider, defaults.Provider),
            ApifyBaseUrl: NormalizeText(persisted.ApifyBaseUrl, defaults.ApifyBaseUrl),
            ApifyActorId: NormalizeText(persisted.ApifyActorId, defaults.ApifyActorId),
            ApifyApiToken: decryptedToken,
            ApifyRequestTimeoutSeconds: persisted.ApifyRequestTimeoutSeconds > 0
                ? persisted.ApifyRequestTimeoutSeconds
                : defaults.ApifyRequestTimeoutSeconds);
    }

    private void Persist(InstagramRuntimeSettings settings)
    {
        EnsureStorageExists();

        var document = new InstagramSettingsDocument
        {
            Provider = settings.Provider,
            ApifyBaseUrl = settings.ApifyBaseUrl,
            ApifyActorId = settings.ApifyActorId,
            EncryptedApifyApiToken = string.IsNullOrWhiteSpace(settings.ApifyApiToken)
                ? string.Empty
                : _credentialVault.Encrypt(settings.ApifyApiToken),
            ApifyRequestTimeoutSeconds = settings.ApifyRequestTimeoutSeconds
        };

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(document, JsonOptions));
    }

    private void EnsureStorageExists()
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static string ResolveSettingsPath(string configuredPath, string contentRootPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(contentRootPath, configuredPath);
    }

    private static string NormalizeText(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeProvider(string? value, string fallback)
    {
        var provider = NormalizeText(value, fallback);
        return provider.Equals("Apify", StringComparison.OrdinalIgnoreCase) ? "Apify"
            : provider.Equals("Rpa", StringComparison.OrdinalIgnoreCase) ? "Rpa"
            : provider.Equals("Mock", StringComparison.OrdinalIgnoreCase) ? "Mock"
            : fallback;
    }

    private static InstagramSettingsView ToView(InstagramRuntimeSettings settings)
    {
        return new InstagramSettingsView(
            settings.Provider,
            settings.ApifyBaseUrl,
            settings.ApifyActorId,
            !string.IsNullOrWhiteSpace(settings.ApifyApiToken),
            settings.ApifyRequestTimeoutSeconds);
    }

    private sealed class InstagramSettingsDocument
    {
        public string Provider { get; set; } = string.Empty;
        public string ApifyBaseUrl { get; set; } = string.Empty;
        public string ApifyActorId { get; set; } = string.Empty;
        public string EncryptedApifyApiToken { get; set; } = string.Empty;
        public int ApifyRequestTimeoutSeconds { get; set; }
    }
}

public sealed record InstagramRuntimeSettings(
    string Provider,
    string ApifyBaseUrl,
    string ApifyActorId,
    string ApifyApiToken,
    int ApifyRequestTimeoutSeconds);

public sealed record InstagramSettingsView(
    string Provider,
    string ApifyBaseUrl,
    string ApifyActorId,
    bool HasApifyApiToken,
    int ApifyRequestTimeoutSeconds);

public sealed record InstagramSettingsUpdate(
    string Provider,
    string ApifyBaseUrl,
    string ApifyActorId,
    string? ApifyApiToken,
    int ApifyRequestTimeoutSeconds,
    bool ClearApifyApiToken);
