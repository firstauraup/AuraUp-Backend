namespace AuraUpBack.Domain.Services;

public interface IVideoTranscriptionService
{
    Task<VideoTranscriptionResult> TranscribeAsync(string videoUrl, string caption, CancellationToken cancellationToken);
}

public sealed record VideoTranscriptionResult(
    string Transcript,
    string TranscriptHook,
    string TranscriptScript);
