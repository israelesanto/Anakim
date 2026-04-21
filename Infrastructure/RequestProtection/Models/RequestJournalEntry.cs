namespace AnakimOrchestrator.Infrastructure.RequestProtection.Models
{
    public class RequestJournalEntry
    {
        public string EventType { get; set; } = string.Empty;

        public string RequestId { get; set; } = string.Empty;

        public DateTime TimestampUtc { get; set; }

        public string? Status { get; set; }

        public string? ApplicationHandlerInstanceName { get; set; }

        public int AttemptNumber { get; set; }

        public string? Message { get; set; }

        public string? PayloadJson { get; set; }
    }
}