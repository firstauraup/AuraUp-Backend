namespace AuraUpBack.Infrastructure.Options;

public sealed class AuraUpBackStorageOptions
{
    public const string SectionName = "AuraUpBack";

    public string DataPath { get; set; } = "App_Data/aura-up-back.json";
    public string InstagramSettingsPath { get; set; } = "App_Data/instagram-settings.json";
    public int MonitoringLoopSeconds { get; set; } = 10;
    public int DefaultCheckEveryMinutes { get; set; } = 60;
}
