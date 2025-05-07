using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Anakim.Infrastructure;

namespace Anakim.TrafficManager
{
    // Service that runs in Traffic Manager mode (Mode = 1) and listens for statistics from Proxy Instances
    public class TrafficManagerService : IHostedService
    {
        private readonly IConfiguration _configuration;
        private TcpListener _listener;
        private int _port;
        private readonly InstanceRankingManager _rankingManager = new();

        // Constructor validates configuration and sets up port and ranking manager
        public TrafficManagerService(IConfiguration configuration, InstanceRankingManager rankingManager)
        {
            _configuration = configuration;
            _rankingManager = rankingManager;

            var settings = _configuration.GetSection("ProxySettings");
            if (settings.GetValue<int>("Mode") != 1)
                throw new InvalidOperationException("TrafficManagerService should only run in Traffic Manager mode (Mode: 1).");

            _port = settings.GetValue<int>("Port");
            if (_port <= 0)
                throw new ArgumentException($"Port must be greater than 0. Current value: {_port}", nameof(_port));
        }

        // Starts listening on the configured port for incoming connections from Proxy Instances
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
                    _ = HandleClientAsync(client, cancellationToken); // Handle client in background
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error accepting client connection: {ex.Message}");
                }
            }
        }

        // Gracefully stops the listener
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _listener?.Stop();
            Logger.LogInfo("Traffic Manager Service stopped.");
            return Task.CompletedTask;
        }

        // Handles an individual Proxy Instance connection, receiving and processing statistics
        private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            NodeStatistics? lastStats = null;

            try
            {
                var remote = client?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
                Logger.LogInfo($"Connection established with {remote}");

                var stream = client.GetStream();
                var buffer = new byte[2048];

                while (!cancellationToken.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    if (bytesRead == 0)
                    {
                        Logger.LogWarning($"Client {remote} disconnected.");
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    var stats = JsonSerializer.Deserialize<NodeStatistics>(message);

                    // If valid statistics were received
                    if (stats != null && !string.IsNullOrEmpty(stats.ProcessStat?.InstanceId))
                    {
                        lastStats = stats;
                        _rankingManager.Update(stats);

                        Logger.LogInfo($"Statistics updated for {stats.ProcessStat.InstanceName} [{stats.ProcessStat.InstanceId}]");

                        var best = _rankingManager.GetBestInstance();
                        if (best != null)
                        {
                            Logger.LogInfo($"🟢 Top ranked: {best.ProcessStat.InstanceName} | CPU: {best.ProcessStat.CpuUsage} | Memory: {best.ProcessStat.PrivateMemoryMB}MB");
                        }
                    }
                    else
                    {
                        Logger.LogWarning("Invalid or incomplete statistics received.");
                    }

                    // Responds to the Proxy Instance with an ACK
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
                var remote = client?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
                client?.Close();
                Logger.LogInfo($"Connection closed for {remote}");

                // Removes the instance from ranking upon disconnection
                if (lastStats?.ProcessStat?.InstanceId != null)
                {
                    var removed = _rankingManager.Remove(lastStats.ProcessStat.InstanceId);
                    if (removed)
                        Logger.LogInfo($"✅ Instance {lastStats.ProcessStat.InstanceName} removed from ranking.");
                    else
                        Logger.LogWarning($"⚠️ Failed to remove: {lastStats.ProcessStat.InstanceId} not found in ranking.");
                }
            }
        }

        // Exposes the current best-ranked Proxy Instance
        public NodeStatistics? GetBestProxyInstance()
        {
            return _rankingManager.GetBestInstance();
        }
    }
}
