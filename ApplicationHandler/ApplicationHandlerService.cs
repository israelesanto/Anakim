using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Anakim.Infrastructure
{
    public class ApplicationHandlerService : BackgroundService
    {
        private readonly string _piHost;
        private readonly int _piPort;
        private readonly string _instanceId;
        private readonly string _instanceName;
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly IConfiguration _configuration;
        private readonly INodeStatisticsService _nodeStatisticsService;
        private readonly ProxySettings _proxySettings;


        public ApplicationHandlerService(IConfiguration configuration, INodeStatisticsService nodeStatisticsService)
        {
            _configuration = configuration;
            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>();

            // Acessa os dados do ProxyInstance
            var proxyInstanceSettings = configuration.GetSection("ProxySettings:ProxyInstance");
            _piHost = proxyInstanceSettings.GetValue<string>("Host");
            _piPort = proxyInstanceSettings.GetValue<int>("Port");

            // Acessa os dados gerais do ProxySettings
            //var proxySettings = configuration.GetSection("ProxySettings");
            //_instanceId = _proxySettings.GetValue<string>("InstanceId");
            //_instanceName = _proxySettings.GetValue<string>("InstanceName");
            _instanceId = _proxySettings.InstanceId;
            _instanceName = _proxySettings.InstanceName;

            if (string.IsNullOrEmpty(_piHost) || _piPort <= 0)
            {
                throw new InvalidOperationException("Invalid Proxy Instance configuration in ProxySettings.");
            }

            if (string.IsNullOrEmpty(_instanceId) || string.IsNullOrEmpty(_instanceName))
            {
                throw new InvalidOperationException("InstanceId or InstanceName is not configured in ProxySettings.");
            }

            _nodeStatisticsService = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
        }

        public async Task ConnectToProxyInstance(CancellationToken stoppingToken)
        {
            Logger.LogInfo("Starting connection to Proxy Instance...");
            await ExecuteConnectionAsync(stoppingToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("ApplicationHandlerService started.");
            await ExecuteConnectionAsync(stoppingToken);
        }

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

                    await SendStatisticsPeriodically(stoppingToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to connect to Proxy Instance: {ex.Message}");
                    CleanupConnection();
                    await Task.Delay(5000, stoppingToken); // Retry connection after delay
                }
            }
        }

        private async Task SendStatisticsPeriodically(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var statistics = _nodeStatisticsService.CollectStatistics();
                    var message = JsonSerializer.Serialize(statistics);
                    var data = Encoding.UTF8.GetBytes(message);

                    await _stream.WriteAsync(data, stoppingToken);
                    //Logger.LogInfo($"Statistics sent to Proxy Instance: {message}");
                    Logger.LogInfo($"Statistics sent to Proxy Instance: {statistics.ProcessStat.InstanceName}");

                    await Task.Delay(_proxySettings.TimeUpdate, stoppingToken); // Send stats every 5 seconds
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending statistics: {ex.Message}");
                CleanupConnection();
            }
        }

        private double GetProcessCpuUsage(Process process)
        {
            var totalProcessorTime = process.TotalProcessorTime.TotalMilliseconds;
            var elapsedMilliseconds = Environment.TickCount - process.StartTime.ToUniversalTime().Millisecond;
            return (totalProcessorTime / elapsedMilliseconds) * 100 / Environment.ProcessorCount;
        }

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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        private void CleanupConnection()
        {
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;
            Logger.LogWarning("Connection to Proxy Instance cleaned up.");
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.LogInfo("ApplicationHandlerService is stopping...");
            CleanupConnection();
            await base.StopAsync(cancellationToken);
        }
    }
}
