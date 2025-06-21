using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace AnakimOrchestrator.Infrastructure
{
    // Service responsible for collecting statistics about the current process and system
    public class NodeStatisticsService : INodeStatisticsService
    {
        private readonly IConfiguration _configuration;
        private readonly ProxySettings _proxySettings;

        // Constructor that loads proxy settings from configuration
        public NodeStatisticsService(IConfiguration configuration)
        {
            _configuration = configuration;
            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                ?? throw new InvalidOperationException("ProxySettings not configured.");
        }

        // Gathers and returns statistics for this running instance
        public NodeStatistics CollectStatistics()
        {
            var process = Process.GetCurrentProcess();

            return new NodeStatistics
            {
                Timestamp = DateTime.UtcNow,
                SenderIp = GetLocalIpAddress(),

                SenderPort = new NodeStatistics.PortsInfo
                {
                    GeneralPort = _proxySettings.Port,
                },

                Port = new NodeStatistics.PortsInfo
                {
                    GeneralPort = _configuration.GetValue<int>("GeneralPort"),
                },

                ProcessStat = new NodeStatistics.ProcessStatistics
                {
                    Name = "Proxy Instance",
                    CpuUsage = Math.Round(GetCpuUsage(), 2),
                    MemoryUsageMB = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2),
                    PrivateMemoryMB = Math.Round(process.PrivateMemorySize64 / (1024.0 * 1024.0), 2),
                    ActiveThreads = process.Threads.Count,
                    InstanceId = _proxySettings?.InstanceId ?? "unknow",
                    InstanceName = _proxySettings?.InstanceName ?? "unknow"
                },

                System = new NodeStatistics.SystemStatistics
                {
                    CpuUsage = Math.Round(GetSystemCpuUsage(), 2),
                    MemoryAvailableMB = Math.Max(0, Math.Round(GetAvailableMemory(), 2)),
                    TotalMemoryMB = Math.Max(0, Math.Round(GetTotalMemory(), 2))
                }
            };
        }


        // Tries to obtain a non-loopback IPv4 address of the current machine
        private string GetLocalIpAddress()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    {
                        return ip.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error retrieving local IP address: {ex.Message}");
            }

            Logger.LogWarning("Could not determine local IP address, defaulting to 127.0.0.1");
            return "127.0.0.1";
        }

        // Returns current process CPU usage percentage
        private double GetCpuUsage()
        {
            var process = Process.GetCurrentProcess();
            var startCpuTime = process.TotalProcessorTime;
            var startTime = DateTime.UtcNow;

            System.Threading.Thread.Sleep(500);

            var endCpuTime = process.TotalProcessorTime;
            var endTime = DateTime.UtcNow;

            var cpuUsedMs = (endCpuTime - startCpuTime).TotalMilliseconds;
            var elapsedMs = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (elapsedMs * Environment.ProcessorCount);

            return Math.Round(cpuUsageTotal * 100, 2);
        }

        // Returns overall CPU usage of the system
        private double GetSystemCpuUsage()
        {
            var process = Process.GetCurrentProcess();
            var startCpuTime = process.TotalProcessorTime;
            var startTime = DateTime.UtcNow;

            System.Threading.Thread.Sleep(500);

            var endCpuTime = process.TotalProcessorTime;
            var endTime = DateTime.UtcNow;

            var cpuUsedMs = (endCpuTime - startCpuTime).TotalMilliseconds;
            var elapsedMs = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (elapsedMs * Environment.ProcessorCount);

            return Math.Round(cpuUsageTotal * 100, 2);
        }

        // Returns estimated available memory based on .NET GC info
        private double GetAvailableMemory()
        {
            var gcMemory = GC.GetGCMemoryInfo();
            return gcMemory.TotalAvailableMemoryBytes / (1024.0 * 1024.0);
        }

        // Returns total memory used by the heap (does not include all system memory)
        private double GetTotalMemory()
        {
            var gcMemory = GC.GetGCMemoryInfo();
            return gcMemory.HeapSizeBytes / (1024.0 * 1024.0);
        }
    }
}
