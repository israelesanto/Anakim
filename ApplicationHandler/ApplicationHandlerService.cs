using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AnakimOrchestrator.Infrastructure
{
    public class ApplicationHandlerService : BackgroundService
    {
        private readonly string? _piHost;
        private readonly int _piPort;
        private readonly bool _hasProxyInstance;
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

            _hasProxyInstance = _proxySettings.HasProxyInstance;

            if (_hasProxyInstance)
            {
                var proxyInstanceSettings = configuration.GetSection("ProxySettings:ProxyInstance");

                _piHost = proxyInstanceSettings.GetValue<string>("Host")
                    ?? throw new InvalidOperationException("The 'Host' section is missing or malformed in appsettings.json.");

                _piPort = proxyInstanceSettings.GetValue<int>("Port");

                if (string.IsNullOrEmpty(_piHost) || _piPort <= 0)
                    throw new InvalidOperationException("Invalid Proxy Instance configuration in ProxySettings.");
            }

            _instanceId = _proxySettings.InstanceId ?? "unknown";
            _instanceName = _proxySettings.InstanceName ?? "unknown";

            if (string.IsNullOrEmpty(_instanceId) || string.IsNullOrEmpty(_instanceName))
                throw new InvalidOperationException("InstanceId or InstanceName is not configured in ProxySettings.");

            _nodeStatisticsService = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
        }

        public async Task ConnectToProxyInstance(CancellationToken stoppingToken)
        {
            if (_hasProxyInstance)
            {
                Logger.LogInfo("Starting connection to Proxy Instance...");
                await ExecuteConnectionAsync(stoppingToken);
            }
            else
            {
                Logger.LogInfo("HasProxyInstance is false, skipping connection to Proxy Instance.");
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("ApplicationHandlerService started.");
            if (_hasProxyInstance)
                await ExecuteConnectionAsync(stoppingToken);
            else
                Logger.LogInfo("HasProxyInstance is false, skipping automatic connection to Proxy Instance.");
        }

        private async Task ExecuteConnectionAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Logger.LogInfo($"Attempting to connect to Proxy Instance at {_piHost}:{_piPort}...");
                    _client = new TcpClient();
                    await _client.ConnectAsync(_piHost!, _piPort, stoppingToken);

                    _stream = _client.GetStream();
                    Logger.LogSuccess("Connected to Proxy Instance!");

                    // 🔔 HELLO: informa porta/host/esquema públicos ao PI (uma única vez por conexão)
                    await SendHelloAsync(_stream, _configuration, _proxySettings);

                    // ▶️ loop de métricas
                    await SendStatisticsPeriodically(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to connect to Proxy Instance: {ex.Message}");
                    CleanupConnection();
                    await Task.Delay(5000, stoppingToken);
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

                    await Task.Delay(_proxySettings.TimeUpdate, stoppingToken);
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

        private async Task SendHelloAsync(NetworkStream stream, IConfiguration cfg, ProxySettings proxy)
        {
            var useHttps = cfg.GetValue<bool>("UseHttps");
            var publicPort = cfg.GetValue<int>("GeneralPort"); // porta pública em que o AH está ouvindo (ex.: 8001/8016)
            var publicHost = GetFirstNonLoopbackIPv4() ?? "localhost";

            var hello = new
            {
                InstanceId = proxy.InstanceId,       // "AH01"
                InstanceName = proxy.InstanceName,   // "Application Handler 01"
                PublicHost = publicHost,
                PublicPort = publicPort,
                UseHttps = useHttps
            };

            var json = System.Text.Json.JsonSerializer.Serialize(hello);
            var data = Encoding.UTF8.GetBytes(json);
            await stream.WriteAsync(data, 0, data.Length);
            Logger.LogInfo($"[HELLO→PI] {hello.InstanceId} {publicHost}:{publicPort} https={(useHttps ? "on" : "off")}");
        }

        private static string? GetFirstNonLoopbackIPv4()
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !System.Net.IPAddress.IsLoopback(ua.Address))
                    {
                        return ua.Address.ToString();
                    }
                }
            }
            return null;
        }
    }
}
