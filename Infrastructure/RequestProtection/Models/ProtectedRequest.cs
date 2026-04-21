using AnakimOrchestrator.Infrastructure.RequestProtection.Enums;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Models
{
    public class ProtectedRequest
    {
        public string RequestId { get; set; } = string.Empty;

        public string CorrelationId { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public DateTime? CompletedAtUtc { get; set; }

        public string HttpMethod { get; set; } = string.Empty;

        public string Route { get; set; } = string.Empty;

        public string QueryString { get; set; } = string.Empty;

        public Dictionary<string, string> Headers { get; set; } = new();

        public string Body { get; set; } = string.Empty;

        public string BodyHash { get; set; } = string.Empty;

        public ProtectedRequestStatus Status { get; set; } = ProtectedRequestStatus.Received;

        public RequestReplayPolicy ReplayPolicy { get; set; } = RequestReplayPolicy.RequiresIdempotency;

        public RequestProcessingKind ProcessingKind { get; set; } = RequestProcessingKind.Command;

        public string? SelectedApplicationHandler { get; set; }

        public int AttemptCount { get; set; }

        public DateTime? LastAttemptAtUtc { get; set; }

        public string? LastError { get; set; }

        public bool IsStateUncertain { get; set; }

        public int? ResponseStatusCode { get; set; }

        public string? ResponseBody { get; set; }

        public DateTime? ExpiresAtUtc { get; set; }

        public List<ProtectedRequestAttempt> Attempts { get; set; } = new();
    }
}