using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace AccessPoint.Infrastructure
{
    public class TrafficManagerService : IHostedService
    {
        private readonly IConfiguration _configuration;
        private TcpListener _listener;
        private int _port;

        // Armazena estatísticas recebidas de cada PI
        private readonly Dictionary<string, NodeStatistics> _nodeStatistics = new();

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

                    // Respond to the client
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
                if (stats == null || string.IsNullOrEmpty(stats.SenderIp))
                {
                    Logger.LogWarning("Invalid or incomplete statistics received.");
                    return;
                }

                // Update the statistics for the corresponding PI
                _nodeStatistics[stats.SenderIp] = stats;
                Logger.LogInfo($"Statistics updated for {stats.SenderIp}");

                // Example log for debugging
                Logger.LogInfo(JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error processing statistics: {ex.Message}");
            }
        }
    }
}
