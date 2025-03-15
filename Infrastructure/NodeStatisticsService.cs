using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace AccessPoint.Infrastructure
{
    public class NodeStatisticsService : INodeStatisticsService
    {
        private readonly IConfiguration _configuration;
        private readonly ProxySettings _proxySettings;

        public NodeStatisticsService(IConfiguration configuration)
        {
            _configuration = configuration;
            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>() ?? throw new InvalidOperationException("ProxySettings not configured.");
        }

        public NodeStatistics CollectStatistics()
        {
            var generalPorts = _configuration.GetSection("GeneralPorts");
            var process = Process.GetCurrentProcess();

            return new NodeStatistics
            {
                Timestamp = DateTime.UtcNow,
                SenderIp = GetLocalIpAddress(),
                SenderPorts = new NodeStatistics.PortsInfo
                {
                    Api = _proxySettings.Port,
                    Page = 0,
                    Socket = 0
                },
                Ports = new NodeStatistics.PortsInfo
                {
                    Api = generalPorts.GetValue<int>("PortApi"),
                    Page = generalPorts.GetValue<int>("PortPage"),
                    Socket = generalPorts.GetValue<int>("PortSocket")
                },
                ProcessStat = new NodeStatistics.ProcessStatistics
                {
                    Name = "Proxy Instance",
                    CpuUsage = Math.Round(GetCpuUsage(), 2),
                    MemoryUsageMB = Math.Round(process.WorkingSet64 / (1024.0 * 1024.0), 2),
                    PrivateMemoryMB = Math.Round(process.PrivateMemorySize64 / (1024.0 * 1024.0), 2),
                    ActiveThreads = process.Threads.Count,
                    InstanceId = _proxySettings.InstanceId,
                    InstanceName = _proxySettings.InstanceName
                },
                System = new NodeStatistics.SystemStatistics
                {
                    CpuUsage = Math.Round(GetSystemCpuUsage(), 2),
                    MemoryAvailableMB = Math.Max(0, Math.Round(GetAvailableMemory(), 2)),
                    TotalMemoryMB = Math.Max(0, Math.Round(GetTotalMemory(), 2))
                }
            };
        }

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

        private double GetCpuUsage()
        {
            using var cpuCounter = new PerformanceCounter("Process", "% Processor Time", Process.GetCurrentProcess().ProcessName, true);
            cpuCounter.NextValue();
            System.Threading.Thread.Sleep(500);
            return cpuCounter.NextValue() / Environment.ProcessorCount;
        }

        private double GetSystemCpuUsage()
        {
            using var systemCpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            systemCpuCounter.NextValue();
            System.Threading.Thread.Sleep(500);
            return systemCpuCounter.NextValue();
        }

        private double GetAvailableMemory()
        {
            var gcMemory = GC.GetGCMemoryInfo();
            return gcMemory.TotalAvailableMemoryBytes / (1024.0 * 1024.0);
        }

        private double GetTotalMemory()
        {
            var gcMemory = GC.GetGCMemoryInfo();
            return gcMemory.HeapSizeBytes / (1024.0 * 1024.0);
        }
    }
}
