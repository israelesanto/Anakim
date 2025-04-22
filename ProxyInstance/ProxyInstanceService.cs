using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using AccessPoint.Infrastructure;
using System.Collections.Concurrent;
using Anakim.Infrastructure;

namespace AccessPoint.Infrastructure
{
    public class ProxyInstanceService : BackgroundService
    {
        private readonly string _tmHost;
        private readonly int _tmPort;
        private readonly int _piPort;
        private TcpClient _client;
        private NetworkStream _stream;
        private TcpListener _listener;
        private readonly IConfiguration _configuration;
        private readonly ProxySettings _proxySettings;
        private readonly INodeStatisticsService _nodeStatisticsService;
        private readonly InstanceRankingManager _rankingManager = new();

        public ProxyInstanceService(IConfiguration configuration, INodeStatisticsService nodeStatisticsService)
        {
            _configuration = configuration;

            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>();
            if (_proxySettings == null)
                throw new InvalidOperationException("ProxySettings is not configured properly in appsettings.json.");

            _piPort = _proxySettings.Port;
            if (_piPort <= 0)
                throw new InvalidOperationException("Invalid Proxy Instance port configuration.");

            var tmSettings = configuration.GetSection("ProxySettings:TrafficManager");
            _tmHost = tmSettings.GetValue<string>("Host");
            _tmPort = tmSettings.GetValue<int>("Port");

            if (string.IsNullOrEmpty(_tmHost) || _tmPort <= 0)
                throw new InvalidOperationException("Invalid Traffic Manager configuration in ProxySettings.");

            _nodeStatisticsService = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("ProxyInstanceService started.");
            StartListener(stoppingToken);
            await ExecuteConnectionAsync(stoppingToken);
        }

        private void StartListener(CancellationToken stoppingToken)
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, _piPort);
                _listener.Start();
                Logger.LogSuccess($"Proxy Instance listening on port {_piPort} for Application Handlers.");
                Task.Run(() => AcceptClientsAsync(stoppingToken));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to start Proxy Instance listener on port {_piPort}: {ex.Message}");
            }
        }

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

        private async Task HandleClientAsync(TcpClient client)
        {
            NodeStatistics? ultimoStatsRecebido = null;

            try
            {
                if (client?.Client != null)
                    Logger.LogInfo($"Application Handler connected: {client.Client.RemoteEndPoint}");
                else
                    Logger.LogInfo("Application Handler connected (socket was null)");

                var stream = client.GetStream();
                var buffer = new byte[2048];

                while (true)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));

                    if (bytesRead == 0)
                    {
                        if (client?.Client != null)
                            Logger.LogWarning($"Client {client.Client.RemoteEndPoint} disconnected.");
                        else
                            Logger.LogWarning("Client disconnected (socket was null).");

                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Logger.LogInfo($"Received from AH: {message}");

                    var stats = ProcessStatistics(message);
                    if (stats != null)
                        ultimoStatsRecebido = stats;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error handling client: {ex.Message}");
            }
            finally
            {
                client?.Close();

                if (client?.Client != null)
                    Logger.LogInfo($"Connection closed for {client.Client.RemoteEndPoint}");
                else
                    Logger.LogInfo("Connection closed for unknown client (socket was null)");

                if (ultimoStatsRecebido?.ProcessStat?.InstanceId != null)
                {
                    var removed = _rankingManager.Remove(ultimoStatsRecebido.ProcessStat.InstanceId);
                    if (removed)
                        Logger.LogInfo($"✅ Instance {ultimoStatsRecebido.ProcessStat.InstanceName} removida do ranking.");
                    else
                        Logger.LogWarning($"⚠️ Falha ao remover: {ultimoStatsRecebido.ProcessStat.InstanceId} não encontrado no ranking.");
                }
            }
        }

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
                    ValidateStatistics(statistics);

                    var message = JsonSerializer.Serialize(statistics);
                    var data = Encoding.UTF8.GetBytes(message);

                    await _stream.WriteAsync(data, stoppingToken);
                    Logger.LogInfo($"Statistics sent to Traffic Manager: {message}");

                    await Task.Delay(5000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending statistics: {ex.Message}");
                CleanupConnection();
            }
        }

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

                Logger.LogInfo($"Statistics received from AH: {JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true })}");

                _rankingManager.Update(stats);

                var melhor = _rankingManager.GetBestInstance();
                if (melhor != null)
                {
                    Logger.LogInfo($"🟢 Melhor no ranking até agora: {melhor.ProcessStat.InstanceName} | CPU: {melhor.ProcessStat.CpuUsage} | Memória: {melhor.ProcessStat.MemoryUsageMB}MB");
                }

                return stats;
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error processing statistics: {ex.Message}");
                return null;
            }
        }

        public NodeStatistics? GetBestApplicationHandler()
        {
            return _rankingManager.GetBestInstance();
        }

        private void CleanupConnection()
        {
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;
            Logger.LogWarning("Connection to Traffic Manager cleaned up.");
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.LogInfo("ProxyInstanceService is stopping...");
            _listener?.Stop();
            CleanupConnection();
            await base.StopAsync(cancellationToken);
        }

        private void ValidateStatistics(NodeStatistics statistics)
        {
            if (statistics.ProcessStat.CpuUsage < 0)
                statistics.ProcessStat.CpuUsage = 0;

            if (statistics.System.MemoryAvailableMB < 0)
                statistics.System.MemoryAvailableMB = 0;

            if (statistics.System.TotalMemoryMB < 0)
                statistics.System.TotalMemoryMB = 0;
        }

        public async Task ConnectToTrafficManager(CancellationToken stoppingToken)
        {
            Logger.LogInfo("Starting manual connection to Traffic Manager...");
            await ExecuteConnectionAsync(stoppingToken);
        }
    }
}
