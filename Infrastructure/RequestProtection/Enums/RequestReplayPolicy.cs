namespace AnakimOrchestrator.Infrastructure.RequestProtection.Enums
{
    public enum RequestReplayPolicy
    {
        SafeReplay = 0,
        RequiresIdempotency = 1,
        NoReplay = 2,
        RequiresReconciliation = 3
    }
}