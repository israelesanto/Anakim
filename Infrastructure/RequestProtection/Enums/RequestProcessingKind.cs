namespace AnakimOrchestrator.Infrastructure.RequestProtection.Enums
{
    public enum RequestProcessingKind
    {
        ReadOnly = 0,
        Query = 1,
        Command = 2,
        Integration = 3
    }
}