namespace AuraUpBack.Domain.Exceptions;

public sealed class InstagramRateLimitException(string message) : InvalidOperationException(message)
{
}
