using AuraUpBack.Domain.Models;

namespace AuraUpBack.Infrastructure.Abstractions;

internal interface IInstagramInspectionProvider
{
    string Name { get; }

    Task<InspectionPayload> InspectAccountAsync(
        InstagramInspectionRequest request,
        CancellationToken cancellationToken);
}
