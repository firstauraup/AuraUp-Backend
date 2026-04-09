namespace AuraUpBack.Infrastructure.Options;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1/messages";
    public string Model { get; set; } = "claude-sonnet-4-20250514";
    public int MaxTokens { get; set; } = 16000;
    public int RequestTimeoutSeconds { get; set; } = 300;
}
