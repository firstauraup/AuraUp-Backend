using Microsoft.Extensions.Configuration;

namespace AuraUpBack.Infrastructure.Options;

public sealed class MinioMediaOptions
{
    public string PrivateEndpoint { get; set; } = string.Empty;
    public string PublicEndpoint { get; set; } = string.Empty;
    public string RootUser { get; set; } = string.Empty;
    public string RootPassword { get; set; } = string.Empty;
    public string BucketName { get; set; } = "auraup-media";
    public int SignedUrlMinutes { get; set; } = 20;
    public int MaxParallelUploads { get; set; } = 4;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PrivateEndpoint) &&
        !string.IsNullOrWhiteSpace(PublicEndpoint) &&
        !string.IsNullOrWhiteSpace(RootUser) &&
        !string.IsNullOrWhiteSpace(RootPassword) &&
        !string.IsNullOrWhiteSpace(BucketName);

    public static MinioMediaOptions FromConfiguration(IConfiguration configuration)
    {
        var options = new MinioMediaOptions
        {
            PrivateEndpoint = Read(configuration, "MediaStorage:PrivateEndpoint", "MINIO_PRIVATE_ENDPOINT"),
            PublicEndpoint = Read(configuration, "MediaStorage:PublicEndpoint", "MINIO_PUBLIC_ENDPOINT"),
            RootUser = Read(configuration, "MediaStorage:RootUser", "MINIO_ROOT_USER"),
            RootPassword = Read(configuration, "MediaStorage:RootPassword", "MINIO_ROOT_PASSWORD"),
            BucketName = Read(configuration, "MediaStorage:BucketName", "MINIO_BUCKET_NAME", "auraup-media"),
            SignedUrlMinutes = ReadInt(configuration, "MediaStorage:SignedUrlMinutes", "MINIO_SIGNED_URL_MINUTES", 20),
            MaxParallelUploads = ReadInt(configuration, "MediaStorage:MaxParallelUploads", "MINIO_MAX_PARALLEL_UPLOADS", 4)
        };

        return options;
    }

    private static string Read(IConfiguration configuration, string primaryKey, string fallbackKey, string fallback = "")
    {
        return configuration[primaryKey]?.Trim()
            ?? configuration[fallbackKey]?.Trim()
            ?? fallback;
    }

    private static int ReadInt(IConfiguration configuration, string primaryKey, string fallbackKey, int fallback)
    {
        var raw = configuration[primaryKey] ?? configuration[fallbackKey];
        return int.TryParse(raw, out var parsed) && parsed > 0
            ? parsed
            : fallback;
    }
}
