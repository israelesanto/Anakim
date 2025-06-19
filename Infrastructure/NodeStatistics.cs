using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net;
using System.Threading;

namespace Anakim.Infrastructure
{
    public class NodeStatistics
    {
        public string? SenderIp { get; set; } // IP de quem enviou as estatísticas
        public PortsInfo? SenderPort { get; set; } // Porta de quem enviou as estatísticas
        public DateTime Timestamp { get; set; }
        public ProcessStatistics? ProcessStat { get; set; }
        public SystemStatistics? System { get; set; }
        public PortsInfo? Port { get; set; }

        public class PortsInfo
        {
            public int GeneralPort { get; set; }
        }

        public class ProcessStatistics
        {
            public string? Name { get; set; }
            public double CpuUsage { get; set; }
            public double MemoryUsageMB { get; set; }
            public double PrivateMemoryMB { get; set; }
            public int ActiveThreads { get; set; }
            public string InstanceId { get; set; } = string.Empty;
            public string? InstanceName { get; set; }
        }

        public class SystemStatistics
        {
            public double CpuUsage { get; set; }
            public double MemoryAvailableMB { get; set; }
            public double TotalMemoryMB { get; set; }
        }
    }
}