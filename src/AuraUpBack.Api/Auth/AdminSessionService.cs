using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Api.Auth;

public sealed class AdminSessionService(IOptions<AdminAuthOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AdminAuthOptions _options = options.Value;

    public bool ValidateCredentials(string username, string password)
    {
        return string.Equals(username?.Trim(), _options.Username, StringComparison.OrdinalIgnoreCase)
               && string.Equals(password, _options.Password, StringComparison.Ordinal);
    }

    public AuthenticatedAdminSession CreateSession()
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(15, _options.TokenLifetimeMinutes));
        var payload = new SessionPayload(_options.Username, "admin", expiresAtUtc);
        var token = SignPayload(payload);

        return new AuthenticatedAdminSession(
            token,
            payload.Username,
            payload.Role,
            payload.ExpiresAtUtc);
    }

    public bool TryValidateToken(string token, out ValidatedAdminSession session)
    {
        session = default!;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        var payloadBytes = TryDecode(parts[0]);
        var signatureBytes = TryDecode(parts[1]);

        if (payloadBytes is null || signatureBytes is null)
        {
            return false;
        }

        var expectedSignature = ComputeSignature(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(signatureBytes, expectedSignature))
        {
            return false;
        }

        SessionPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<SessionPayload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload is null || payload.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return false;
        }

        session = new ValidatedAdminSession(payload.Username, payload.Role, payload.ExpiresAtUtc);
        return true;
    }

    private string SignPayload(SessionPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signatureBytes = ComputeSignature(payloadBytes);

        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signatureBytes)}";
    }

    private byte[] ComputeSignature(byte[] payloadBytes)
    {
        var signingKeyBytes = Encoding.UTF8.GetBytes(_options.SigningKey);
        using var hmac = new HMACSHA256(signingKeyBytes);
        return hmac.ComputeHash(payloadBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[]? TryDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;

        if (padding > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
        }

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record SessionPayload(string Username, string Role, DateTime ExpiresAtUtc);
}

public sealed record AuthenticatedAdminSession(
    string AccessToken,
    string Username,
    string Role,
    DateTime ExpiresAtUtc);

public sealed record ValidatedAdminSession(
    string Username,
    string Role,
    DateTime ExpiresAtUtc);
