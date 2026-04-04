using System.Text.Json;

namespace AuraUpBack.Api.Auth;

public sealed class AdminAuthMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context, AdminSessionService sessionService)
    {
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            await next(context);
            return;
        }

        if (!context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        if (context.Request.Path.StartsWithSegments("/api/auth/login", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var authorizationHeader = context.Request.Headers.Authorization.ToString();

        if (!TryGetBearerToken(authorizationHeader, out var token) ||
            !sessionService.TryValidateToken(token, out var session))
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized, "Unauthorized", "A valid admin session is required.");
            return;
        }

        context.Items[AdminHttpContextItemKeys.Session] = session;
        await next(context);
    }

    private static bool TryGetBearerToken(string authorizationHeader, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return false;
        }

        const string bearerPrefix = "Bearer ";
        if (!authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        token = authorizationHeader[bearerPrefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(token);
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string error, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            error,
            message
        }, JsonOptions));
    }
}

public static class AdminHttpContextItemKeys
{
    public const string Session = "AuraUpBack.AdminSession";
}
