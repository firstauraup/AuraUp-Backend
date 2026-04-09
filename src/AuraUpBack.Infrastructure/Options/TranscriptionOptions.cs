namespace AuraUpBack.Infrastructure.Options;

public sealed class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    public string Provider { get; set; } = "ClipTranscribe";
    public string ClipTranscribeBaseUrl { get; set; } = "https://cliptranscribe.com/";
    public int RequestTimeoutSeconds { get; set; } = 90;
    public bool Headless { get; set; } = true;
}
