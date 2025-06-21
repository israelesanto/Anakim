using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AnakimOrchestrator.Infrastructure
{
    // Background service that runs the Application Handler role
    public class ApplicationHandlerService : BackgroundService
    {
        private readonly string _piHost;
        private readonly int _piPort;
        private readonly string _instanceId;
        private readonly string _instanceName;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly IConfiguration _configuration;
        private readonly INodeStatisticsService _nodeStatisticsService;
        private readonly ProxySettings _proxySettings;

        public ApplicationHandlerService(IConfiguration configuration, INodeStatisticsService nodeStatisticsService)
        {
            _configuration = configuration;

            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                ?? throw new InvalidOperationException("The 'ProxySettings' section is missing or malformed in appsettings.json.");

            var proxyInstanceSettings = configuration.GetSection("ProxySettings:ProxyInstance");

            _piHost = proxyInstanceSettings.GetValue<string>("Host")
                ?? throw new InvalidOperationException("The 'Host' section is missing or malformed in appsettings.json.");

            _piPort = proxyInstanceSettings.GetValue<int>("Port");

            _instanceId = _proxySettings.InstanceId ?? "unknown";
            _instanceName = _proxySettings.InstanceName ?? "unknown";

            if (string.IsNullOrEmpty(_piHost) || _piPort <= 0)
                throw new InvalidOperationException("Invalid Proxy Instance configuration in ProxySettings.");

            if (string.IsNullOrEmpty(_instanceId) || string.IsNullOrEmpty(_instanceName))
                throw new InvalidOperationException("InstanceId or InstanceName is not configured in ProxySettings.");

            _nodeStatisticsService = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
        }


        // Can be triggered manually to start connection (alternative entry point)
        public async Task ConnectToProxyInstance(CancellationToken stoppingToken)
        {
            Logger.LogInfo("Starting connection to Proxy Instance...");
            await ExecuteConnectionAsync(stoppingToken);
        }

        // Default background service execution entry point
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("ApplicationHandlerService started.");
            await ExecuteConnectionAsync(stoppingToken);
        }

        // Handles the connection and reconnection loop to the Proxy Instance
        private async Task ExecuteConnectionAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Logger.LogInfo($"Attempting to connect to Proxy Instance at {_piHost}:{_piPort}...");
                    _client = new TcpClient();
                    await _client.ConnectAsync(_piHost, _piPort, stoppingToken);

                    _stream = _client.GetStream();
                    Logger.LogSuccess("Connected to Proxy Instance!");

                    // Begins sending statistics periodically
                    await SendStatisticsPeriodically(stoppingToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to connect to Proxy Instance: {ex.Message}");
                    CleanupConnection();
                    await Task.Delay(5000, stoppingToken); // Wait before retrying
                }
            }
        }

        // Sends system and process statistics in JSON format to the Proxy Instance
        private async Task SendStatisticsPeriodically(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var statistics = _nodeStatisticsService.CollectStatistics();
                    var message = JsonSerializer.Serialize(statistics);
                    var data = Encoding.UTF8.GetBytes(message);

                    if (statistics.ProcessStat is null)
                    {
                        Logger.LogError("statistics.ProcessStat is not initialized.");
                        return;
                    }

                    if (_stream is null)
                    {
                        Logger.LogError("Network stream is not initialized.");
                        return;
                    }

                    await _stream.WriteAsync(data, stoppingToken);
                    Logger.LogInfo($"Statistics sent to Proxy Instance: {statistics.ProcessStat.InstanceName}");

                    // Wait based on configured interval (e.g., 5000 ms)
                    await Task.Delay(_proxySettings.TimeUpdate, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending statistics: {ex.Message}");
                CleanupConnection();
            }
        }

        // Not currently used, but would estimate CPU usage for a process
        private double GetProcessCpuUsage(Process process)
        {
            var totalProcessorTime = process.TotalProcessorTime.TotalMilliseconds;
            var elapsedMilliseconds = Environment.TickCount - process.StartTime.ToUniversalTime().Millisecond;
            return (totalProcessorTime / elapsedMilliseconds) * 100 / Environment.ProcessorCount;
        }

        // Struct for querying system memory statistics (used with GlobalMemoryStatusEx)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        // External Win32 API function to get memory usage
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // Cleans up the TCP connection and network stream
        private void CleanupConnection()
        {
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;
            Logger.LogWarning("Connection to Proxy Instance cleaned up.");
        }

        // Called when the service is stopping
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.LogInfo("ApplicationHandlerService is stopping...");
            CleanupConnection();
            await base.StopAsync(cancellationToken);
        }
    }
}
