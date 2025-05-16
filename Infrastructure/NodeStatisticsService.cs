using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace Anakim.Infrastructure
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
            var generalPorts = _configuration.GetSection("GeneralPorts");
            var process = Process.GetCurrentProcess(); // Gets the current process

            return new NodeStatistics
            {
                Timestamp = DateTime.UtcNow, // Current timestamp
                SenderIp = GetLocalIpAddress(), // IP of this machine

                SenderPorts = new NodeStatistics.PortsInfo
                {
                    Api = _proxySettings.Port, // Port used to receive stats (used by TM/PI)
                    Page = 0,
                    Socket = 0
                },

                Ports = new NodeStatistics.PortsInfo
                {
                    Api = generalPorts.GetValue<int>("PortApi"),   // API listening port
                    Page = generalPorts.GetValue<int>("PortPage"), // Web page port
                    Socket = generalPorts.GetValue<int>("PortSocket") // WebSocket port
                },

                ProcessStat = new NodeStatistics.ProcessStatistics
                {
                    Name = "Proxy Instance",
                    CpuUsage = Math.Round(GetCpuUsage(), 2), // CPU usage of current process
                    MemoryUsageMB = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2), // RAM in use
                    PrivateMemoryMB = Math.Round(process.PrivateMemorySize64 / (1024.0 * 1024.0), 2), // Private memory
                    ActiveThreads = process.Threads.Count, // Number of threads in current process
                    InstanceId = _proxySettings?.InstanceId ?? "unknow", // Unique ID of this node
                    InstanceName = _proxySettings?.InstanceName ?? "unknow" // Friendly name of this node
                },

                System = new NodeStatistics.SystemStatistics
                {
                    CpuUsage = Math.Round(GetSystemCpuUsage(), 2), // Total CPU usage on the machine
                    MemoryAvailableMB = Math.Max(0, Math.Round(GetAvailableMemory(), 2)), // Free memory
                    TotalMemoryMB = Math.Max(0, Math.Round(GetTotalMemory(), 2)) // Total heap size (GC)
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
