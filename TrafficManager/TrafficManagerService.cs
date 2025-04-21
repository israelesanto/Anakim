using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using AccessPoint.Infrastructure;
using Anakim.Infrastructure;

namespace AccessPoint.Infrastructure
{
    public class TrafficManagerService : IHostedService
    {
        private readonly IConfiguration _configuration;
        private TcpListener _listener;
        private int _port;

        // Ranking de Proxy Instances
        private readonly InstanceRankingManager _rankingManager = new();

        public TrafficManagerService(IConfiguration configuration)
        {
            _configuration = configuration;
            var settings = _configuration.GetSection("ProxySettings");
            if (settings.GetValue<int>("Mode") != 1)
                throw new InvalidOperationException("TrafficManagerService should only run in Traffic Manager mode (Mode: 1).");

            _port = settings.GetValue<int>("Port");
            if (_port <= 0)
                throw new ArgumentException($"Port must be greater than 0. Current value: {_port}", nameof(_port));
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            Logger.LogSuccess($"Traffic Manager listening on port {_port}");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Logger.LogInfo("Waiting for connections...");
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleClientAsync(client, cancellationToken);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error accepting client connection: {ex.Message}");
                }
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _listener?.Stop();
            Logger.LogInfo("Traffic Manager Service stopped.");
            return Task.CompletedTask;
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            Logger.LogInfo($"Connection established with {client.Client.RemoteEndPoint}");
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[2048];

                while (!cancellationToken.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                    if (bytesRead == 0)
                    {
                        Logger.LogWarning($"Client {client.Client.RemoteEndPoint} disconnected.");
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Logger.LogInfo($"Received: {message}");

                    ProcessStatistics(message);

                    var response = Encoding.UTF8.GetBytes("ACK");
                    await stream.WriteAsync(response, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error handling client: {ex.Message}");
            }
            finally
            {
                client.Close();
                Logger.LogInfo($"Connection closed for {client.Client.RemoteEndPoint}");
            }
        }

        private void ProcessStatistics(string jsonMessage)
        {
            try
            {
                var stats = JsonSerializer.Deserialize<NodeStatistics>(jsonMessage);
                if (stats == null || string.IsNullOrEmpty(stats.ProcessStat?.InstanceId))
                {
                    Logger.LogWarning("Invalid or incomplete statistics received.");
                    return;
                }

                _rankingManager.Update(stats);
                Logger.LogInfo($"Statistics updated for {stats.ProcessStat.InstanceName} [{stats.ProcessStat.InstanceId}]");

                Logger.LogInfo(JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));

                var melhor = _rankingManager.GetBestInstance();
                if (melhor != null)
                {
                    Logger.LogInfo($"🟢 Melhor no ranking até agora: {melhor.ProcessStat.InstanceName} | CPU: {melhor.ProcessStat.CpuUsage} | Memória: {melhor.ProcessStat.MemoryUsageMB}MB");
                }

            }
            catch (Exception ex)
            {
                Logger.LogError($"Error processing statistics: {ex.Message}");
            }
        }

        public NodeStatistics? GetBestProxyInstance()
        {
            return _rankingManager.GetBestInstance();
        }
    }
}
