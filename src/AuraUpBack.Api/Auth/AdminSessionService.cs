using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Api.Auth;

public sealed class AdminSessionService(
    IOptions<AdminAuthOptions> options,
    IAppUserRepository userRepository,
    PasswordHasher passwordHasher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AdminAuthOptions _options = options.Value;

    public async Task<AuthenticatedAdminSession?> ValidateCredentialsAsync(string usernameOrEmail, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var user = await userRepository.GetByEmailAsync(usernameOrEmail, cancellationToken);
        if (user is not null && user.Status == AppUserStatus.Active && passwordHasher.Verify(password, user.PasswordHash))
        {
            user.RecordLogin(DateTime.UtcNow);
            await userRepository.UpsertAsync(user, cancellationToken);
            return CreateSession(user);
        }

        var bootstrapMatches =
            (string.Equals(usernameOrEmail.Trim(), _options.Username, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(usernameOrEmail.Trim(), _options.Email, StringComparison.OrdinalIgnoreCase))
            && string.Equals(password, _options.Password, StringComparison.Ordinal);

        if (!bootstrapMatches)
        {
            return null;
        }

        var bootstrapUser = user ?? await EnsureBootstrapAdminAsync(cancellationToken);
        bootstrapUser.RecordLogin(DateTime.UtcNow);
        await userRepository.UpsertAsync(bootstrapUser, cancellationToken);
        return CreateSession(bootstrapUser);
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

        session = new ValidatedAdminSession(
            payload.UserId,
            payload.Email,
            payload.Role,
            payload.ExpiresAtUtc);
        return true;
    }

    private AuthenticatedAdminSession CreateSession(AppUser user)
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(Math.Max(15, _options.TokenLifetimeMinutes));
        var payload = new SessionPayload(
            user.Id,
            user.Email,
            user.Role.ToString().ToLowerInvariant(),
            expiresAtUtc);

        return new AuthenticatedAdminSession(
            SignPayload(payload),
            user.Id,
            user.Email,
            payload.Role,
            payload.ExpiresAtUtc);
    }

    private async Task<AppUser> EnsureBootstrapAdminAsync(CancellationToken cancellationToken)
    {
        var email = string.IsNullOrWhiteSpace(_options.Email) ? $"{_options.Username}@auraup.local" : _options.Email;
        var existing = await userRepository.GetByEmailAsync(email, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var nowUtc = DateTime.UtcNow;
        var bootstrapUser = AppUser.CreateInvited(email, AppUserRole.Administrator, nowUtc);
        bootstrapUser.Activate(
            "Admin",
            "AuraUp",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            passwordHasher.Hash(_options.Password),
            nowUtc);
        await userRepository.UpsertAsync(bootstrapUser, cancellationToken);
        return bootstrapUser;
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
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
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

    private sealed record SessionPayload(Guid UserId, string Email, string Role, DateTime ExpiresAtUtc);
}

public sealed record AuthenticatedAdminSession(
    string AccessToken,
    Guid UserId,
    string Email,
    string Role,
    DateTime ExpiresAtUtc);

public sealed record ValidatedAdminSession(
    Guid UserId,
    string Email,
    string Role,
    DateTime ExpiresAtUtc);
