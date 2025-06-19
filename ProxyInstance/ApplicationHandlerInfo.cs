public class ApplicationHandlerInfo
{
    public string InstanceId { get; set; } = string.Empty;             // Unique ID of the AH
    public string Url { get; set; } = string.Empty;                    // Base URL for forwarding
    public double Ranking { get; set; }                                // Priority for routing
    public DateTime LastFailureTime { get; set; }                      // Last time this AH failed
    public bool TemporarilyUnavailable { get; set; }                   // Marked unavailable due to failure

    public string InstanceName { get; set; } = string.Empty;           // ✅ Friendly name for logs
}
