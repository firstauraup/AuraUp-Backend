using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;

namespace AuraUpBack.Application.Commands.TranscribeTrackedPost;

public sealed record TranscribeTrackedPostCommand(Guid AccountId, Guid PostId) : Abstractions.ICommand<TranscriptionResultDto>;

internal sealed class TranscribeTrackedPostCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IVideoTranscriptionService videoTranscriptionService)
    : Abstractions.ICommandHandler<TranscribeTrackedPostCommand, TranscriptionResultDto>
{
    public async Task<TranscriptionResultDto> HandleAsync(TranscribeTrackedPostCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        var post = account.Posts.FirstOrDefault(x => x.Id == command.PostId)
            ?? throw new InvalidOperationException("Tracked post was not found.");

        var transcript = await videoTranscriptionService.TranscribeAsync(post.Url, post.Caption, cancellationToken);
        post.SetTranscript(transcript, DateTime.UtcNow);

        await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        return new TranscriptionResultDto(account.Id, post.Id, transcript);
    }
}
