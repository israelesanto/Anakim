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
    public class ProxyInstanceService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly string? _tmHost;
        private readonly int _tmPort;
        private readonly int _piPort;
        private readonly bool _hasTrafficManager;
        private TcpClient? _client;
        private NetworkStream? _stream;
        private TcpListener _listener;
        private readonly ProxySettings _proxySettings;
        private readonly INodeStatisticsService _nodeStatisticsService;
        private readonly InstanceRankingManager _rankingManager;

        public ProxyInstanceService(IConfiguration configuration, INodeStatisticsService nodeStatisticsService, InstanceRankingManager rankingManager)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                ?? throw new InvalidOperationException("ProxySettings is not configured properly in appsettings.json.");

            _hasTrafficManager = _proxySettings.HasTrafficManager;
            _piPort = _proxySettings.Port;

            if (_piPort <= 0)
                throw new InvalidOperationException("Invalid Proxy Instance port configuration.");

            if (_hasTrafficManager)
            {
                var tmSettings = configuration.GetSection("ProxySettings:TrafficManager");
                _tmHost = tmSettings.GetValue<string>("Host") ?? throw new InvalidOperationException("Traffic Manager host is missing in configuration.");
                _tmPort = tmSettings.GetValue<int>("Port");

                if (string.IsNullOrEmpty(_tmHost) || _tmPort <= 0)
                    throw new InvalidOperationException("Invalid Traffic Manager configuration in ProxySettings.");
            }

            _nodeStatisticsService = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
            _rankingManager = rankingManager ?? throw new ArgumentNullException(nameof(rankingManager));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("ProxyInstanceService started.");
            StartListener(stoppingToken);

            if (_hasTrafficManager)
                await ExecuteConnectionAsync(stoppingToken);
            else
                Logger.LogInfo("HasTrafficManager is false, skipping connection to Traffic Manager.");
        }

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
                var buffer = new byte[8192];

                // ---- Primeiro pacote: tenta HELLO do AH
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                if (bytesRead == 0)
                {
                    Logger.LogWarning($"Client {remoteInfo} disconnected (no initial data).");
                    return;
                }

                var firstMessage = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                if (!TryProcessHelloFromAh(firstMessage, client))
                {
                    // Não era HELLO: tenta processar como estatística
                    var stats0 = ProcessStatistics(firstMessage);
                    if (stats0 != null) lastReceivedStats = stats0;
                }

                // ---- Loop normal
                while (true)
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
                    if (bytesRead == 0)
                    {
                        Logger.LogWarning($"Client {remoteInfo} disconnected.");
                        break;
                    }

                    var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    // Aceita HELLOs idempotentes
                    if (TryProcessHelloFromAh(message, client))
                        continue;

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

        // HELLO recebido do AH: registra endpoint público e marca como vivo
        private bool TryProcessHelloFromAh(string jsonMessage, TcpClient client)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                var root = doc.RootElement;

                // Campos aceitos no HELLO do AH
                string? id = root.TryGetProperty("InstanceId", out var pId) ? pId.GetString() : null;
                string? name = root.TryGetProperty("InstanceName", out var pName) ? pName.GetString() : null;
                string? host = root.TryGetProperty("PublicHost", out var pHost) ? pHost.GetString() : null;

                int port = 0;
                if (root.TryGetProperty("PublicPort", out var pPort) && pPort.ValueKind == JsonValueKind.Number)
                    port = pPort.GetInt32();
                else if (root.TryGetProperty("GeneralPort", out var gPort) && gPort.ValueKind == JsonValueKind.Number)
                    port = gPort.GetInt32();

                bool useHttps = root.TryGetProperty("UseHttps", out var pHttps) && pHttps.ValueKind == JsonValueKind.True;

                if (string.IsNullOrWhiteSpace(id))
                    return false; // não é HELLO

                // host fallback = IP remoto
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "127.0.0.1";
                var chosenHost = string.IsNullOrWhiteSpace(host) ? remoteIp : host!.Trim();

                // porta é obrigatória; se não vier, não é HELLO válido
                if (port <= 0)
                {
                    Logger.LogWarning($"[HANDSHAKE PI] HELLO do {id} sem porta pública (PublicPort/GeneralPort). Ignorado.");
                    return true; // era HELLO mas inválido → evita cair no parser de métricas
                }

                var scheme = useHttps ? "https" : "http";
                var baseUrl = $"{scheme}://{chosenHost}:{port}";

                _rankingManager.RegisterPublicEndpoint(id!, baseUrl);
                _rankingManager.TouchAlive(id!);

                Logger.LogInfo($"[HANDSHAKE PI] Registrado endpoint público do {id} ({name ?? "AH"}) → {baseUrl}");
                return true;
            }
            catch
            {
                return false;
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
                    await _client.ConnectAsync(_tmHost!, _tmPort, stoppingToken);

                    _stream = _client.GetStream();
                    Logger.LogSuccess("Connected to Traffic Manager!");

                    // (Opcional) Se quiser também mandar HELLO do PI ao TM, faça aqui.
                    await SendHelloToTrafficManagerAsync(_stream, _configuration, _proxySettings);

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

        // PI -> TM: HELLO com porta/host públicos do PI
        private async Task SendHelloToTrafficManagerAsync(NetworkStream stream, IConfiguration cfg, ProxySettings proxy)
        {
            try
            {
                var useHttps = cfg.GetValue<bool>("UseHttps");
                var publicPort = cfg.GetValue<int>("GeneralPort"); // porta pública do PI (onde o Kestrel do PI está ouvindo)
                var publicHost = GetFirstNonLoopbackIPv4() ?? "localhost";

                var hello = new
                {
                    InstanceId = proxy.InstanceId,     // "PI01"
                    InstanceName = proxy.InstanceName,   // "Proxy Instance 01"
                    PublicHost = publicHost,
                    PublicPort = publicPort,
                    UseHttps = useHttps
                };

                var json = JsonSerializer.Serialize(hello);
                var data = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(data, 0, data.Length);
                Logger.LogInfo($"[HELLO→TM] {hello.InstanceId} {publicHost}:{publicPort} https={(useHttps ? "on" : "off")}");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[HELLO→TM] falhou: {ex.Message}");
            }
        }

        private static string? GetFirstNonLoopbackIPv4()
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(ua.Address))
                    {
                        return ua.Address.ToString();
                    }
                }
            }
            return null;
        }

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
                        Logger.LogError("Stream is null.");
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
                if (best?.ProcessStat == null)
                    return stats;

                Logger.LogInfo($"🟢 Top ranked: {best.ProcessStat.InstanceName} | CPU: {best.ProcessStat.CpuUsage} | Memory: {best.ProcessStat.PrivateMemoryMB}MB");
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

        public async Task ConnectToTrafficManager(CancellationToken stoppingToken)
        {
            if (_hasTrafficManager)
            {
                Logger.LogInfo("Starting manual connection to Traffic Manager...");
                await ExecuteConnectionAsync(stoppingToken);
            }
            else
            {
                Logger.LogInfo("HasTrafficManager is false, skipping manual connection.");
            }
        }
    }
}
