using AuraUpBack.Domain.Models;

namespace AuraUpBack.Domain.Services;

public interface IInstagramResearchAutomation
{
    Task<InspectionPayload> InspectAccountAsync(
        InstagramInspectionRequest request,
        CancellationToken cancellationToken);
}
