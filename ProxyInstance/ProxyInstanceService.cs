using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.ProxyInstance
{
    // This background service acts as a Proxy Instance: it listens to Application Handlers and reports stats to the Traffic Manager
    public class ProxyInstanceService : BackgroundService
    {
        private readonly string _tmHost;
        private readonly int _tmPort;
        private readonly int _piPort;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private TcpListener _listener;
        private readonly ProxySettings _proxySettings;
        private readonly INodeStatisticsService _nodeStatisticsService;
        private readonly InstanceRankingManager _rankingManager = new();

        public ProxyInstanceService(IConfiguration configuration, INodeStatisticsService nodeStatisticsService, InstanceRankingManager rankingManager)
        {
            _listener = new TcpListener(IPAddress.Any, _piPort);
            _rankingManager = rankingManager;
            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                ?? throw new InvalidOperationException("ProxySettings is not configured properly in appsettings.json.");

            _piPort = _proxySettings.Port;
            if (_piPort <= 0)
                throw new InvalidOperationException("Invalid Proxy Instance port configuration.");

            var tmSettings = configuration.GetSection("ProxySettings:TrafficManager");
            _tmHost = tmSettings.GetValue<string>("Host") ?? throw new InvalidOperationException("Traffic Manager host is missing in configuration.");
            _tmPort = tmSettings.GetValue<int>("Port");

            if (string.IsNullOrEmpty(_tmHost) || _tmPort <= 0)
                throw new InvalidOperationException("Invalid Traffic Manager configuration in ProxySettings.");

            _nodeStatisticsService = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
        }

        // Entry point for the background service
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("ProxyInstanceService started.");
            StartListener(stoppingToken);                // Start listening for Application Handlers
            await ExecuteConnectionAsync(stoppingToken); // Connect to the Traffic Manager
        }

        // Starts the TCP listener for receiving statistics from Application Handlers
        private void StartListener(CancellationToken stoppingToken)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _piPort);
                _listener.Start();
                Logger.LogSuccess($"Proxy Instance listening on port {_piPort} for Application Handlers.");
                Task.Run(() => AcceptClientsAsync(stoppingToken), stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to start Proxy Instance listener on port {_piPort}: {ex.Message}");
            }
        }

        // Accepts incoming TCP clients asynchronously
        private async Task AcceptClientsAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                    _ = HandleClientAsync(client);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogInfo("Accepting clients canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error accepting client connection: {ex.Message}");
                }
            }
        }

        // Handles communication with a connected Application Handler
        private async Task HandleClientAsync(TcpClient client)
        {
            NodeStatistics? lastReceivedStats = null;

            try
            {
                var remoteInfo = client?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
                Logger.LogInfo($"Application Handler connected: {remoteInfo}");

                if (client is null)
                {
                    Logger.LogError("TcpClient é nulo. Encerrando execução.");
                    return;
                }

                var stream = client.GetStream();
                var buffer = new byte[2048];

                while (true)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    if (bytesRead == 0)
                    {
                        Logger.LogWarning($"Client {remoteInfo} disconnected.");
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var stats = ProcessStatistics(message);
                    if (stats != null)
                        lastReceivedStats = stats;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error handling client: {ex.Message}");
            }
            finally
            {
                var remoteInfo = client?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
                client?.Close();
                Logger.LogInfo($"Connection closed for {remoteInfo}");

                if (lastReceivedStats?.ProcessStat?.InstanceId != null)
                {
                    var removed = _rankingManager.Remove(lastReceivedStats.ProcessStat.InstanceId);
                    if (removed)
                        Logger.LogInfo($"✅ Instance {lastReceivedStats.ProcessStat.InstanceName} removed from ranking.");
                    else
                        Logger.LogWarning($"⚠️ Failed to remove: {lastReceivedStats.ProcessStat.InstanceId} not found in ranking.");
                }
            }
        }

        // Connects to the Traffic Manager and sends this instance's statistics periodically
        private async Task ExecuteConnectionAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Logger.LogInfo($"Attempting to connect to Traffic Manager at {_tmHost}:{_tmPort}...");
                    _client = new TcpClient();
                    await _client.ConnectAsync(_tmHost, _tmPort, stoppingToken);

                    _stream = _client.GetStream();
                    Logger.LogSuccess("Connected to Traffic Manager!");

                    await SendStatisticsPeriodically(stoppingToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Failed to connect to Traffic Manager: {ex.Message}");
                    CleanupConnection();
                    await Task.Delay(_proxySettings.TimeUpdate, stoppingToken);
                }
            }
        }

        // Sends process and system statistics to the Traffic Manager continuously
        private async Task SendStatisticsPeriodically(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var statistics = _nodeStatisticsService.CollectStatistics();
                    if (statistics.ProcessStat is null)
                    {
                        Logger.LogError("statistics.ProcessStat is null.");
                        return;
                    }

                    ValidateStatistics(statistics);

                    var message = JsonSerializer.Serialize(statistics);
                    var data = Encoding.UTF8.GetBytes(message);

                    if (_stream is null)
                    {
                        Logger.LogError("Stram is null.");
                        return;
                    }

                    await _stream.WriteAsync(data, stoppingToken);
                    Logger.LogInfo($"Statistics sent to Traffic Manager: {statistics.ProcessStat.InstanceName}");

                    await Task.Delay(_proxySettings.TimeUpdate, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending statistics: {ex.Message}");
                CleanupConnection();
            }
        }

        // Deserializes and updates the ranking with the received statistics
        private NodeStatistics? ProcessStatistics(string jsonMessage)
        {
            try
            {
                var stats = JsonSerializer.Deserialize<NodeStatistics>(jsonMessage);
                if (stats == null)
                {
                    Logger.LogWarning("Invalid or incomplete statistics received.");
                    return null;
                }

                _rankingManager.Update(stats);

                var best = _rankingManager.GetBestInstance();

                if (best == null)
                {
                    Logger.LogInfo("The 'best' object is null");
                    return null;
                }

                if (best.ProcessStat == null)
                {
                    Logger.LogInfo("The 'best.ProcessStat' object is null");
                    return null;
                }

                if (best != null)
                {
                    Logger.LogInfo($"🟢 Top ranked: {best.ProcessStat.InstanceName} | CPU: {best.ProcessStat.CpuUsage} | Memory: {best.ProcessStat.PrivateMemoryMB}MB");
                }

                return stats;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error processing statistics: {ex.Message}");
                return null;
            }
        }

        // Exposes the best-ranked Application Handler to other components
        public NodeStatistics? GetBestApplicationHandler()
        {
            return _rankingManager.GetBestInstance();
        }

        // Cleans up the connection to the Traffic Manager
        private void CleanupConnection()
        {
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;
            Logger.LogWarning("Connection to Traffic Manager cleaned up.");
        }

        // Called when the service is stopping
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.LogInfo("ProxyInstanceService is stopping...");
            _listener?.Stop();
            CleanupConnection();
            await base.StopAsync(cancellationToken);
        }

        // Sanitizes the statistics to prevent invalid negative values
        private static void ValidateStatistics(NodeStatistics statistics)
        {
            if (statistics.ProcessStat == null)
            {
                Logger.LogInfo("The 'statistics.ProcessStat' object is null");
                return;
            }

            if (statistics.System == null)
            {
                Logger.LogInfo("The 'statistics.System' object is null");
                return;
            }

            if (statistics.ProcessStat.CpuUsage < 0)
                statistics.ProcessStat.CpuUsage = 0;

            if (statistics.System.MemoryAvailableMB < 0)
                statistics.System.MemoryAvailableMB = 0;

            if (statistics.System.TotalMemoryMB < 0)
                statistics.System.TotalMemoryMB = 0;
        }

        // Manual trigger to start connection to Traffic Manager
        public async Task ConnectToTrafficManager(CancellationToken stoppingToken)
        {
            Logger.LogInfo("Starting manual connection to Traffic Manager...");
            await ExecuteConnectionAsync(stoppingToken);
        }
    }
}
