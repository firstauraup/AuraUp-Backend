using System.Security.Cryptography;
using System.Text;
using AuraUpBack.Domain.Entities;
using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace AuraUpBack.Api.Auth;

public sealed class UserInvitationService(
    IUserInvitationRepository invitationRepository,
    IAppUserRepository userRepository,
    PasswordHasher passwordHasher,
    IOptions<AdminAuthOptions> options)
{
    private readonly AdminAuthOptions _options = options.Value;

    public async Task<InvitationIssueResult> InviteAsync(
        string email,
        AppUserRole role,
        IReadOnlyCollection<Guid> accountIds,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var normalizedEmail = AppUser.NormalizeEmail(email);
        var user = await userRepository.GetByEmailAsync(normalizedEmail, cancellationToken)
            ?? AppUser.CreateInvited(normalizedEmail, role, nowUtc);

        user.Role = role;
        user.UpdateAssignments(role == AppUserRole.Client ? accountIds : [], nowUtc);
        await userRepository.UpsertAsync(user, cancellationToken);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var tokenHash = ComputeTokenHash(token);
        var invitation = UserInvitation.Create(
            user.Id,
            normalizedEmail,
            role,
            tokenHash,
            nowUtc.AddHours(Math.Max(1, _options.InvitationLifetimeHours)),
            nowUtc);
        await invitationRepository.AddAsync(invitation, cancellationToken);

        var invitationUrl = $"{_options.PublicAppUrl.TrimEnd('/')}/register/?token={token}";
        return new InvitationIssueResult(user, invitation, invitationUrl);
    }

    public async Task<InvitationView?> GetInvitationAsync(string token, CancellationToken cancellationToken)
    {
        var invitation = await invitationRepository.GetByTokenHashAsync(ComputeTokenHash(token), cancellationToken);
        if (invitation is null)
        {
            return null;
        }

        var user = await userRepository.GetByIdAsync(invitation.UserId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        return new InvitationView(
            invitation.Email,
            invitation.Role,
            invitation.ExpiresAtUtc,
            invitation.AcceptedAtUtc,
            invitation.CanBeAccepted(DateTime.UtcNow));
    }

    public async Task<AppUser> CompleteRegistrationAsync(
        string token,
        string firstName,
        string lastName,
        string phoneNumber,
        string city,
        string country,
        string companyName,
        string password,
        CancellationToken cancellationToken)
    {
        var nowUtc = DateTime.UtcNow;
        var invitation = await invitationRepository.GetByTokenHashAsync(ComputeTokenHash(token), cancellationToken)
            ?? throw new InvalidOperationException("Invitation was not found.");
        if (!invitation.CanBeAccepted(nowUtc))
        {
            throw new InvalidOperationException("Invitation has expired or was already used.");
        }

        var user = await userRepository.GetByIdAsync(invitation.UserId, cancellationToken)
            ?? throw new InvalidOperationException("Invited user was not found.");

        user.Role = invitation.Role;
        user.Activate(
            firstName,
            lastName,
            phoneNumber,
            city,
            country,
            companyName,
            passwordHasher.Hash(password),
            nowUtc);

        await userRepository.UpsertAsync(user, cancellationToken);
        invitation.MarkAccepted(nowUtc);
        await invitationRepository.UpdateAsync(invitation, cancellationToken);
        return user;
    }

    private static string ComputeTokenHash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

public sealed record InvitationIssueResult(
    AppUser User,
    UserInvitation Invitation,
    string InvitationUrl);

public sealed record InvitationView(
    string Email,
    AppUserRole Role,
    DateTime ExpiresAtUtc,
    DateTime? AcceptedAtUtc,
    bool CanBeAccepted);
