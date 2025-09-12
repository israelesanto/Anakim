using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using AnakimOrchestrator.Infrastructure;
using System.Collections.Concurrent;

namespace AnakimOrchestrator.ProxyInstance
{
    public class ProxyInstanceService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly string? _tmHost;
        private readonly int _tmPort;

        // Porta já usada para receber conexões dos AHs (estatísticas/handshake)
        private readonly int _piPort;

        // NOVO: porta de streaming para observabilidade (NDJSON)
        private readonly int _streamPort;

        private readonly bool _hasTrafficManager;
        private TcpClient? _client;
        private NetworkStream? _stream;

        private TcpListener _listener;         // AH -> PI (já existia)
        private TcpListener _obsListener;      // NOVO: Observabilidade -> PI (stream NDJSON)

        private readonly ProxySettings _proxySettings;
        private readonly INodeStatisticsService _nodeStatisticsService;
        private readonly InstanceRankingManager _rankingManager;

        // NOVO: agregador (PI + seus AHs) para montar o payload do stream
        private readonly IProxyStatisticsAggregator _aggregator;

        // NOVO: conexões de observadores inscritos no stream
        private readonly ConcurrentDictionary<Guid, StreamWriter> _observers = new();

        public ProxyInstanceService(
            IConfiguration configuration,
            INodeStatisticsService nodeStatisticsService,
            InstanceRankingManager rankingManager,
            IProxyStatisticsAggregator aggregator // injete no DI
        )
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>()
                ?? throw new InvalidOperationException("ProxySettings is not configured properly in appsettings.json.");

            _hasTrafficManager = _proxySettings.HasTrafficManager;
            _piPort = _proxySettings.Port;

            if (_piPort <= 0)
                throw new InvalidOperationException("Invalid Proxy Instance port configuration.");

            // Porta do TM (se houver)
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
            _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));

            // NOVO: lê ProxySettings:PortStream (porta de stream). Fallback = _piPort + 1
            var cfgStreamPort = configuration.GetSection("ProxySettings").GetValue<int>("PortStream");
            _streamPort = cfgStreamPort > 0 ? cfgStreamPort : (_piPort + 1);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("ProxyInstanceService started.");

            // 1) Listener para AHs (como já era)
            StartListenerForAH(stoppingToken);

            // 2) NOVO: Listener de streaming de observabilidade (clientes externos)
            StartObserverStream(stoppingToken);

            // 3) Se houver TM, mantém o loop de conexão e envio de estatísticas
            if (_hasTrafficManager)
                await ExecuteConnectionAsync(stoppingToken);
            else
                Logger.LogInfo("HasTrafficManager is false, skipping connection to Traffic Manager.");
        }

        // =========================
        // 1) LISTENER PARA AHS (JÁ EXISTIA)
        // =========================
        private void StartListenerForAH(CancellationToken stoppingToken)
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

        // =========================
        // 2) NOVO: STREAM DE OBSERVABILIDADE (CLIENTE → CONECTA E RECEBE NDJSON)
        // =========================
        private void StartObserverStream(CancellationToken stoppingToken)
        {
            try
            {
                _obsListener = new TcpListener(IPAddress.Any, _streamPort);
                _obsListener.Start();
                Logger.LogSuccess($"[STREAM] Observability listening on port {_streamPort} (NDJSON).");

                // Accept loop
                Task.Run(() => AcceptObserversAsync(stoppingToken), stoppingToken);

                // Publisher loop
                Task.Run(() => PublishSnapshotsLoopAsync(stoppingToken), stoppingToken);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[STREAM] Failed to start observability listener on port {_streamPort}: {ex.Message}");
            }
        }

        private async Task AcceptObserversAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var client = await _obsListener.AcceptTcpClientAsync(stoppingToken);
                    _ = HandleObserverAsync(client, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogInfo("[STREAM] Accept observers canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[STREAM] Error accepting observer: {ex.Message}");
                }
            }
        }

        private async Task HandleObserverAsync(TcpClient client, CancellationToken ct)
        {
            StreamWriter w = null!;
            try
            {
                var stream = client.GetStream();
                w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var id = Guid.NewGuid();
                _observers[id] = w;

                // hello_ack imediato
                await w.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "hello_ack",
                    server = "Anakim",
                    role = "PI",
                    ts = DateTime.UtcNow
                }));

                Logger.LogInfo($"[STREAM] Observer connected: {client.Client.RemoteEndPoint}");

                // Heartbeats dedicados, caso fique ocioso
                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        try
                        {
                            await w.WriteLineAsync(JsonSerializer.Serialize(new { type = "heartbeat", ts = DateTime.UtcNow }));
                        }
                        catch
                        {
                            break;
                        }
                    }
                }, ct);

                // Mantém a conexão aberta até o cliente encerrar
                var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break; // cliente fechou
                    // opcional: tratar "ping" ou comandos simples
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[STREAM] Error handling observer: {ex.Message}");
            }
            finally
            {
                try
                {
                    // remove do pool
                    foreach (var kv in _observers)
                    {
                        if (kv.Value == w)
                        {
                            _observers.TryRemove(kv.Key, out _);
                            break;
                        }
                    }
                }
                catch { /* ignore */ }

                try { client?.Close(); } catch { }
                Logger.LogInfo("[STREAM] Observer disconnected.");
            }
        }

        private async Task PublishSnapshotsLoopAsync(CancellationToken ct)
        {
            var lastFull = DateTime.MinValue;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var full = (now - lastFull) >= TimeSpan.FromSeconds(5);

                    // monta snapshot (PI + AHs)
                    var payload = _aggregator.BuildSnapshot(full);
                    var json = JsonSerializer.Serialize(payload);

                    foreach (var kv in _observers.ToArray())
                    {
                        try
                        {
                            await kv.Value.WriteLineAsync(json);
                        }
                        catch
                        {
                            _observers.TryRemove(kv.Key, out _);
                        }
                    }

                    if (full) lastFull = now;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[STREAM] publish error: {ex.Message}");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch { }
            }
        }

        // =========================
        // 3) CONEXÃO COM TRAFFIC MANAGER (JÁ EXISTIA)
        // =========================
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

                    // (Opcional) HELLO do PI ao TM
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
                var publicPort = cfg.GetValue<int>("GeneralPort"); // porta pública do PI (Kestrel)
                var publicHost = GetFirstNonLoopbackIPv4() ?? "localhost";

                var hello = new
                {
                    InstanceId = proxy.InstanceId,     // "PI01"
                    InstanceName = proxy.InstanceName, // "Proxy Instance 01"
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
            try { _listener?.Stop(); } catch { }
            try { _obsListener?.Stop(); } catch { }
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

        // Uso manual caso precise reconectar ao TM via endpoint administrativo
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
