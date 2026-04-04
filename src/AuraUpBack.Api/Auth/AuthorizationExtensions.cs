using AuraUpBack.Domain.Enums;
using AuraUpBack.Domain.Repositories;

namespace AuraUpBack.Api.Auth;

public static class AuthorizationExtensions
{
    public static ValidatedAdminSession RequireSession(this HttpContext httpContext)
    {
        if (httpContext.Items.TryGetValue(AdminHttpContextItemKeys.Session, out var sessionObject) &&
            sessionObject is ValidatedAdminSession session)
        {
            return session;
        }

        throw new InvalidOperationException("Authenticated session was not found.");
    }

    public static bool IsAdministrator(this ValidatedAdminSession session)
    {
        return session.Role.Equals(AppUserRole.Administrator.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWorker(this ValidatedAdminSession session)
    {
        return session.Role.Equals(AppUserRole.Worker.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsClient(this ValidatedAdminSession session)
    {
        return session.Role.Equals(AppUserRole.Client.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static IResult ForbidAction(string message)
    {
        return Results.Json(new { error = "Forbidden", message }, statusCode: StatusCodes.Status403Forbidden);
    }

    public static async Task<bool> CanAccessAccountAsync(
        this ValidatedAdminSession session,
        Guid accountId,
        IAppUserRepository userRepository,
        CancellationToken cancellationToken)
    {
        if (session.IsAdministrator() || session.IsWorker())
        {
            return true;
        }

        var user = await userRepository.GetByIdAsync(session.UserId, cancellationToken);
        return user?.AssignedAccounts.Any(x => x.AccountId == accountId) == true;
    }
}
