using AuraUpBack.Domain.Services;

namespace AuraUpBack.Infrastructure.Services;

internal sealed class MockVideoTranscriptionService : IVideoTranscriptionService
{
    public Task<string> TranscribeAsync(string videoUrl, string caption, CancellationToken cancellationToken)
    {
        var shortCaption = caption.Length > 140 ? $"{caption[..140]}..." : caption;

        var transcript = string.Join(' ', [
            "Hook:",
            shortCaption,
            "Main point:",
            "the creator sets up a clear promise, adds one concrete example, then closes with a short call to action.",
            "Visual pacing:",
            "fast cuts in the first 2 seconds, tighter rhythm in the middle, clean closing line at the end.",
            $"Source: {videoUrl}"
        ]);

        return Task.FromResult(transcript);
    }
}
