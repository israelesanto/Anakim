using AnakimOrchestrator.Infrastructure.RequestProtection.Enums;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Models
{
    public class ProtectedRequestAttempt
    {
        public int AttemptNumber { get; set; }

        public string? ApplicationHandlerInstanceName { get; set; }

        public DateTime StartedAtUtc { get; set; }

        public DateTime? FinishedAtUtc { get; set; }

        public ProtectedRequestAttemptResult Result { get; set; } = ProtectedRequestAttemptResult.None;

        public string? FailureReason { get; set; }

        public bool WasConnectionEstablished { get; set; }

        public bool WasBodySent { get; set; }

        public bool WasAckReceived { get; set; }
    }
}