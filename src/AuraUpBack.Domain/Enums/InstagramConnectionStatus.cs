namespace AuraUpBack.Domain.Enums;

public enum InstagramConnectionStatus
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    VerificationRequired = 3,
    ReconnectRequired = 4,
    Failed = 5
}
