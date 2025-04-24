using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Threading;

namespace Anakim.Infrastructure
{
    public class NodeStatistics
    {
        public string SenderIp { get; set; } // IP de quem enviou as estatísticas
        public PortsInfo SenderPorts { get; set; } // Portas de quem enviou as estatísticas

        public class PortsInfo
        {
            public int Api { get; set; }
            public int Page { get; set; }
            public int Socket { get; set; }
        }

        public class ProcessStatistics
        {
            public string Name { get; set; }
            public double CpuUsage { get; set; }
            public double MemoryUsageMB { get; set; }
            public double PrivateMemoryMB { get; set; }
            public int ActiveThreads { get; set; }
            public string InstanceId { get; set; }
            public string InstanceName { get; set; }
        }

        public class SystemStatistics
        {
            public double CpuUsage { get; set; }
            public double MemoryAvailableMB { get; set; }
            public double TotalMemoryMB { get; set; }
        }

        public DateTime Timestamp { get; set; }
        public ProcessStatistics ProcessStat { get; set; }
        public SystemStatistics System { get; set; }
        public PortsInfo Ports { get; set; }
    }
}