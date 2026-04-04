using AuraUpBack.Infrastructure.Services;

namespace AuraUpBack.Infrastructure.Abstractions;

public interface IInstagramSettingsService
{
    InstagramRuntimeSettings Current { get; }
    Task<InstagramSettingsView> GetViewAsync(CancellationToken cancellationToken);
    Task<InstagramSettingsView> UpdateAsync(InstagramSettingsUpdate update, CancellationToken cancellationToken);
}
