namespace AnakimOrchestrator.Infrastructure.RequestProtection.Enums
{
    public enum ProtectedRequestStatus
    {
        Received = 0,
        Persisted = 1,
        Dispatching = 2,
        InFlight = 3,
        Completed = 4,
        Failed = 5,
        Unknown = 6,
        RetryScheduled = 7,
        Replayed = 8,
        Expired = 9,
        DeadLettered = 10
    }
}