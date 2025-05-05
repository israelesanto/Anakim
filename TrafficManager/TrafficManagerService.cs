using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Anakim.Infrastructure;
using Anakim.Infrastructure;

namespace Anakim.Infrastructure
{
    public class TrafficManagerService : IHostedService
    {
        private readonly IConfiguration _configuration;
        private TcpListener _listener;
        private int _port;

        private readonly InstanceRankingManager _rankingManager = new();

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
            NodeStatistics? ultimoStatsRecebido = null;

            try
            {
                if (client?.Client != null)
                    Logger.LogInfo($"Connection established with {client.Client.RemoteEndPoint}");
                else
                    Logger.LogInfo("Connection established with unknown client (socket was null)");

                var stream = client.GetStream();
                var buffer = new byte[2048];

                while (!cancellationToken.IsCancellationRequested)
                {
                    var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                    if (bytesRead == 0)
                    {
                        if (client?.Client != null)
                            Logger.LogWarning($"Client {client.Client.RemoteEndPoint} disconnected.");
                        else
                            Logger.LogWarning("Client disconnected (socket was null).");

                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    //Logger.LogInfo($"Received: {message}");
                    

                    var stats = JsonSerializer.Deserialize<NodeStatistics>(message);
                    if (stats != null && !string.IsNullOrEmpty(stats.ProcessStat?.InstanceId))
                    {
                        ultimoStatsRecebido = stats;
                        _rankingManager.Update(stats);

                        Logger.LogInfo($"Statistics updated for {stats.ProcessStat.InstanceName} [{stats.ProcessStat.InstanceId}]");
                        //Logger.LogInfo(JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true }));

                        var melhor = _rankingManager.GetBestInstance();
                        if (melhor != null)
                        {
                            Logger.LogInfo($"🟢 Melhor no ranking até agora: {melhor.ProcessStat.InstanceName} | CPU: {melhor.ProcessStat.CpuUsage} | Memória: {melhor.ProcessStat.PrivateMemoryMB}MB");
                        }
                    }
                    else
                    {
                        Logger.LogWarning("Invalid or incomplete statistics received.");
                    }

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


        public NodeStatistics? GetBestProxyInstance()
        {
            return _rankingManager.GetBestInstance();
        }
    }
}
