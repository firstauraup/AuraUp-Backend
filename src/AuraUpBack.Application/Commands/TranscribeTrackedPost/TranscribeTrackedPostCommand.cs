using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Repositories;
using AuraUpBack.Domain.Services;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Application.Commands.TranscribeTrackedPost;

public sealed record TranscribeTrackedPostCommand(Guid AccountId, Guid PostId) : Abstractions.ICommand<TranscriptionResultDto>;

internal sealed class TranscribeTrackedPostCommandHandler(
    ITrackedAccountRepository trackedAccountRepository,
    IVideoTranscriptionService videoTranscriptionService,
    ILogger<TranscribeTrackedPostCommandHandler> logger)
    : Abstractions.ICommandHandler<TranscribeTrackedPostCommand, TranscriptionResultDto>
{
    public async Task<TranscriptionResultDto> HandleAsync(TranscribeTrackedPostCommand command, CancellationToken cancellationToken)
    {
        var account = await trackedAccountRepository.GetByIdAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked account was not found.");

        var post = account.Posts.FirstOrDefault(x => x.Id == command.PostId)
            ?? throw new InvalidOperationException("Tracked post was not found.");

        logger.LogInformation(
            "Transcripción solicitada para account {AccountId} post {PostId} con URL {PostUrl}.",
            account.Id,
            post.Id,
            post.Url);

        var transcript = await videoTranscriptionService.TranscribeAsync(post.Url, post.Caption, cancellationToken);
        post.SetTranscript(transcript, DateTime.UtcNow);

        logger.LogInformation(
            "Guardando transcripción en DB para account {AccountId} post {PostId}. Texto: {Transcript}",
            account.Id,
            post.Id,
            transcript);

        await trackedAccountRepository.UpsertAsync(account, cancellationToken);
        logger.LogInformation(
            "Transcripción guardada en DB para account {AccountId} post {PostId}. TranscriptLength: {TranscriptLength}.",
            account.Id,
            post.Id,
            transcript.Length);

        return new TranscriptionResultDto(account.Id, post.Id, transcript);
    }
}
