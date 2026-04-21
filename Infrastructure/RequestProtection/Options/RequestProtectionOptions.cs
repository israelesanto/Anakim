namespace AnakimOrchestrator.Infrastructure.RequestProtection.Options
{
    public class RequestProtectionOptions
    {
        public bool Enabled { get; set; } = true;

        public string JournalDirectory { get; set; } = "RequestProtection/Journal";

        public string JournalFileNamePrefix { get; set; } = "request-protection";

        public int MaxRetryAttempts { get; set; } = 3;

        public int RequestTtlMinutes { get; set; } = 30;
    }
}