namespace AuraUpBack.Domain.Services;

public interface IVideoTranscriptionService
{
    Task<string> TranscribeAsync(string videoUrl, string caption, CancellationToken cancellationToken);
}
