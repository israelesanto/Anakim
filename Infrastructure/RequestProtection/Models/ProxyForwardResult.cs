namespace AnakimOrchestrator.Infrastructure.RequestProtection.Models
{
    public class ProxyForwardResult
    {
        public bool Success { get; set; }

        public bool ClientCancelled { get; set; }

        public bool ConnectionEstablished { get; set; }

        public bool RequestBodySent { get; set; }

        public bool ResponseHeadersReceived { get; set; }

        public bool ResponseBodyStarted { get; set; }

        public bool IsTimeout { get; set; }

        public bool IsUnknownState { get; set; }

        public int? UpstreamStatusCode { get; set; }

        public string? ErrorMessage { get; set; }
    }
}