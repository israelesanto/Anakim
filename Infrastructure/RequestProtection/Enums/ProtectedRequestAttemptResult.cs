namespace AnakimOrchestrator.Infrastructure.RequestProtection.Enums
{
    public enum ProtectedRequestAttemptResult
    {
        None = 0,
        Success = 1,
        ConnectionFailure = 2,
        TimeoutBeforeSend = 3,
        TimeoutAfterSend = 4,
        HandlerUnavailable = 5,
        ConnectionLost = 6,
        UnknownState = 7,
        Cancelled = 8
    }
}