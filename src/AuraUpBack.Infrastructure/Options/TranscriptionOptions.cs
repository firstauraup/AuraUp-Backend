namespace AuraUpBack.Infrastructure.Options;

public sealed class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    public string Provider { get; set; } = "ClipTranscribe";
    public string ClipTranscribeBaseUrl { get; set; } = "https://cliptranscribe.com/";
    public string ClipTranscribeEmail { get; set; } = string.Empty;
    public string ClipTranscribePassword { get; set; } = string.Empty;
    public string ClipTranscribeSessionStatePath { get; set; } = "App_Data/cliptranscribe-rpa-session.json";
    public string ClipTranscribeSessionStateJson { get; set; } = string.Empty;
    public string ClipTranscribeSessionStateBase64 { get; set; } = string.Empty;
    public int ClipTranscribeLoginTimeoutSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 90;
    public bool Headless { get; set; } = true;
}
