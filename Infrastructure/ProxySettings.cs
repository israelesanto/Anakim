using AnakimOrchestrator.ProxyInstance;
using AnakimOrchestrator.TrafficManager;

namespace AnakimOrchestrator.Infrastructure
{
    public class ProxySettings
    {
        public bool Enable { get; set; }
        public int Mode { get; set; }
        public string? InstanceId { get; set; }
        public string? InstanceName { get; set; }
        public int Port { get; set; }
        public int TimeUpdate { get; set; }
        public int RequestTimeout { get; set; }
        public string? AuthToken { get; set; }
        public bool HasTrafficManager { get; set; }
        public TrafficManagerConfig? TrafficManager { get; set; }
        public bool HasProxyInstance { get; set; }
        public ProxyInstanceConfig? ProxyInstance { get; set; }
    }
}