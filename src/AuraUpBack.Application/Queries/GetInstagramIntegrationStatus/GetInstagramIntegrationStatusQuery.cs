using AuraUpBack.Application.Commands.ConnectInstagramIntegration;
using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Queries.GetInstagramIntegrationStatus;

public sealed record GetInstagramIntegrationStatusQuery() : Abstractions.IQuery<InstagramIntegrationDto>;

internal sealed class GetInstagramIntegrationStatusQueryHandler(
    IInstagramConnectionAutomation instagramConnectionAutomation)
    : Abstractions.IQueryHandler<GetInstagramIntegrationStatusQuery, InstagramIntegrationDto>
{
    public async Task<InstagramIntegrationDto> HandleAsync(GetInstagramIntegrationStatusQuery query, CancellationToken cancellationToken)
    {
        var state = await instagramConnectionAutomation.GetStatusAsync(cancellationToken);
        return state.ToDto();
    }
}
