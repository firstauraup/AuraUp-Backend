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
        var post = await trackedAccountRepository.GetPostByIdAsync(command.AccountId, command.PostId, cancellationToken)
            ?? throw new InvalidOperationException("Tracked post was not found.");

        logger.LogInformation(
            "Transcripción solicitada para account {AccountId} post {PostId} con URL {PostUrl}.",
            command.AccountId,
            post.Id,
            post.Url);

        var transcription = await videoTranscriptionService.TranscribeAsync(post.Url, post.Caption, cancellationToken);

        logger.LogInformation(
            "Guardando transcripción en DB para account {AccountId} post {PostId}. Texto: {Transcript}. Hook: {Hook}",
            command.AccountId,
            post.Id,
            transcription.Transcript,
            transcription.TranscriptHook);

        await trackedAccountRepository.SetPostTranscriptAsync(
            command.AccountId,
            post.Id,
            transcription.Transcript,
            transcription.TranscriptHook,
            transcription.TranscriptScript,
            DateTime.UtcNow,
            cancellationToken);

        logger.LogInformation(
            "Transcripción guardada en DB para account {AccountId} post {PostId}. TranscriptLength: {TranscriptLength}.",
            command.AccountId,
            post.Id,
            transcription.Transcript.Length);

        return new TranscriptionResultDto(
            command.AccountId,
            post.Id,
            transcription.Transcript,
            transcription.TranscriptHook,
            transcription.TranscriptScript);
    }
}
