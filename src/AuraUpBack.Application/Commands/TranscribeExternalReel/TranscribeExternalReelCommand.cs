using AuraUpBack.Application.Contracts;
using AuraUpBack.Domain.Services;
using Microsoft.Extensions.Logging;

namespace AuraUpBack.Application.Commands.TranscribeExternalReel;

public sealed record TranscribeExternalReelCommand(string? ReelUrl) : Abstractions.ICommand<ExternalReelTranscriptionDto>;

internal sealed class TranscribeExternalReelCommandHandler(
    IVideoTranscriptionService videoTranscriptionService,
    ILogger<TranscribeExternalReelCommandHandler> logger)
    : Abstractions.ICommandHandler<TranscribeExternalReelCommand, ExternalReelTranscriptionDto>
{
    public async Task<ExternalReelTranscriptionDto> HandleAsync(TranscribeExternalReelCommand command, CancellationToken cancellationToken)
    {
        var reelUrl = NormalizeReelUrl(command.ReelUrl);
        logger.LogInformation("Transcripción directa solicitada para reel externo {ReelUrl}.", reelUrl);

        var transcription = await videoTranscriptionService.TranscribeAsync(reelUrl, string.Empty, cancellationToken);
        return new ExternalReelTranscriptionDto(
            reelUrl,
            transcription.Transcript,
            transcription.TranscriptHook,
            transcription.TranscriptScript);
    }

    private static string NormalizeReelUrl(string? reelUrl)
    {
        var normalized = reelUrl?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("The reel URL is required.");
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !uri.Host.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Paste a valid Instagram reel URL.");
        }

        if (!uri.AbsolutePath.Contains("/reel/", StringComparison.OrdinalIgnoreCase) &&
            !uri.AbsolutePath.Contains("/reels/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The URL must point to an Instagram reel.");
        }

        return normalized;
    }
}
