using AuraUpBack.Domain.Models;
using AuraUpBack.Domain.Services;
using AuraUpBack.Infrastructure.Abstractions;
using AuraUpBack.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class InstagramResearchAutomation(
    IEnumerable<IInstagramInspectionProvider> providers,
    IOptions<InstagramIntegrationOptions> options,
    IInstagramSettingsService settingsService)
    : IInstagramResearchAutomation
{
    private readonly IReadOnlyDictionary<string, IInstagramInspectionProvider> _providers = providers
        .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    private readonly InstagramIntegrationOptions _options = options.Value;

    public Task<InspectionPayload> InspectAccountAsync(
        InstagramInspectionRequest request,
        CancellationToken cancellationToken)
    {
        var configuredProvider = settingsService.Current.Provider;
        var providerName = string.IsNullOrWhiteSpace(configuredProvider)
            ? (string.IsNullOrWhiteSpace(_options.Provider) ? "Mock" : _options.Provider)
            : configuredProvider;

        if (!_providers.TryGetValue(providerName, out var provider))
        {
            throw new InvalidOperationException(
                $"Instagram provider '{providerName}' is not registered. Available: {string.Join(", ", _providers.Keys.OrderBy(x => x))}.");
        }

        return provider.InspectAccountAsync(request, cancellationToken);
    }
}
