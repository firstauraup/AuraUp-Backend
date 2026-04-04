using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Queries.GetInstagramExplorerAccountPreview;

public sealed record GetInstagramExplorerAccountPreviewQuery(string Handle)
    : Abstractions.IQuery<ExplorerAccountPreviewDto>;

internal sealed class GetInstagramExplorerAccountPreviewQueryHandler(
    IInstagramExplorerService instagramExplorerService)
    : Abstractions.IQueryHandler<GetInstagramExplorerAccountPreviewQuery, ExplorerAccountPreviewDto>
{
    public async Task<ExplorerAccountPreviewDto> HandleAsync(GetInstagramExplorerAccountPreviewQuery query, CancellationToken cancellationToken)
    {
        var preview = await instagramExplorerService.GetAccountPreviewAsync(query.Handle, cancellationToken);
        return new ExplorerAccountPreviewDto(
            preview.Handle,
            preview.DisplayName,
            preview.ProfileImageUrl,
            preview.Bio,
            preview.FollowersCount);
    }
}
