using System.Net;
using System.Net.Sockets;
using System.IO;            
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.TrafficManager
{
    /// <summary>
    /// Traffic Manager (Mode=1)
    /// - Ouve conexões dos PIs em ProxySettings:Port (recebe HELLO + NodeStatistics)
    /// - Expõe um stream NDJSON em ProxySettings:PortStream (ou Port+1) para observabilidade
    ///   que envia snapshots contínuos usando ITrafficManagerStatisticsAggregator
    /// </summary>
    public sealed class TrafficManagerService : BackgroundService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<TrafficManagerService> _logger;
        private readonly InstanceRankingManager _rankingManager;
        private readonly ITrafficManagerStatisticsAggregator _aggregator;

        // Porta para receber conexões dos PIs (já existente)
        private readonly int _tmListenPort;

        // Porta de streaming NDJSON para observabilidade (novo)
        private readonly int _streamPort;

        private TcpListener? _piListener;     // PIs -> TM
        private TcpListener? _obsListener;    // Observability clients -> TM (stream NDJSON)

        // Conexões dos observadores
        private readonly ConcurrentDictionary<Guid, StreamWriter> _observers = new();

        public TrafficManagerService(
            IConfiguration configuration,
            ILogger<TrafficManagerService> logger,
            InstanceRankingManager rankingManager,
            ITrafficManagerStatisticsAggregator aggregator
        )
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rankingManager = rankingManager ?? throw new ArgumentNullException(nameof(rankingManager));
            _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));

            var settings = _configuration.GetSection("ProxySettings");
            if (settings.GetValue<int>("Mode") != 1)
                throw new InvalidOperationException("TrafficManagerService should only run in Traffic Manager mode (Mode: 1).");

            _tmListenPort = settings.GetValue<int>("Port");
            if (_tmListenPort <= 0)
                throw new ArgumentException($"ProxySettings:Port must be greater than 0. Current value: {_tmListenPort}", nameof(_tmListenPort));

            var cfgStreamPort = settings.GetValue<int>("PortStream");
            _streamPort = cfgStreamPort > 0 ? cfgStreamPort : (_tmListenPort + 1);
        }

        private static bool IsNormalDisconnect(Exception ex)
        {
            // cancelamentos e disposes
            if (ex is OperationCanceledException || ex is TaskCanceledException || ex is ObjectDisposedException)
                return true;

            // 10054/ConnectionReset e similares
            if (ex is IOException io && io.InnerException is SocketException se)
            {
                return se.SocketErrorCode is SocketError.ConnectionReset   // 10054
                    or SocketError.ConnectionAborted
                    or SocketError.Shutdown
                    or SocketError.TimedOut;
            }
            return false;
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // 1) Listener para PIs
            StartPiListener(stoppingToken);

            // 2) Listener + publisher do stream NDJSON de observabilidade
            StartObserverStream(stoppingToken);

            // Mantém o serviço vivo até cancelarem
            return Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, stoppingToken);
                }
                catch (OperationCanceledException) { /* normal on shutdown */ }
            }, stoppingToken);
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            try { _piListener?.Stop(); } catch { }
            try { _obsListener?.Stop(); } catch { }
            _logger.LogInformation("Traffic Manager Service stopped.");
            return base.StopAsync(cancellationToken);
        }

        // ========== PIs -> TM (HELLO + NodeStatistics) ==========
        private void StartPiListener(CancellationToken ct)
        {
            try
            {
                _piListener = new TcpListener(IPAddress.Any, _tmListenPort);
                _piListener.Start();
                _logger.LogInformation("Traffic Manager listening on port {Port}", _tmListenPort);

                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        try
                        {
                            _logger.LogInformation("Waiting for PI connections...");
                            var client = await _piListener.AcceptTcpClientAsync(ct);
                            _ = HandlePiClientAsync(client, ct);
                        }
                        catch (OperationCanceledException) { /* shutting down */ }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error accepting PI connection");
                        }
                    }
                }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start PI listener on port {Port}", _tmListenPort);
            }
        }

        private async Task HandlePiClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            NodeStatistics? lastStats = null;
            var remote = client?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
            _logger.LogInformation("Connection established with {Remote}", remote);

            try
            {
                if (client is null)
                {
                    _logger.LogError("TcpClient is null. Aborting.");
                    return;
                }

                using var stream = client.GetStream();
                var buffer = new byte[8192];
                var acc = new StringBuilder();

                while (!cancellationToken.IsCancellationRequested)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // shutdown/cancel normal
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        // EOF: peer fechou “limpo”
                        _logger.LogInformation("Connection closed by {Remote}", remote);
                        break;
                    }

                    acc.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                    foreach (var json in ExtractCompleteJsonObjects(acc))
                    {
                        var payload = json.Trim();
                        if (string.IsNullOrWhiteSpace(payload))
                            continue;

                        // 1) HELLO do PI (idempotente)
                        if (TryProcessHelloFromPi(payload, client))
                            continue;

                        // 2) Estatísticas (NodeStatistics)
                        try
                        {
                            var stats = JsonSerializer.Deserialize<NodeStatistics>(payload);
                            if (stats?.ProcessStat != null)
                            {
                                lastStats = stats;
                                _rankingManager.Update(stats);

                                _logger.LogInformation("Statistics updated for {Name} [{Id}]",
                                    stats.ProcessStat.InstanceName, stats.ProcessStat.InstanceId);

                                var best = _rankingManager.GetBestInstance();
                                if (best?.ProcessStat != null)
                                {
                                    _logger.LogInformation("🟢 Top ranked: {Name} | CPU: {CPU} | Memory: {Mem}MB",
                                        best.ProcessStat.InstanceName, best.ProcessStat.CpuUsage, best.ProcessStat.PrivateMemoryMB);
                                }
                            }
                            else
                            {
                                _logger.LogDebug("NodeStatistics or ProcessStat is null (ignored).");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[TM] Failed to parse message as NodeStatistics");
                            // continua no loop; não derruba o socket
                        }
                    }
                }
            }
            // ⬇️ trata 10054/abort/timeout/etc. como desconexão esperada (não loga como erro)
            catch (Exception ex) when (IsNormalDisconnect(ex))
            {
                _logger.LogInformation("Connection closed for {Remote} (normal disconnect).", remote);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling PI client {Remote}", remote);
            }
            finally
            {
                try { client?.Close(); } catch { }
                _logger.LogInformation("Connection closed for {Remote}", remote);

                // remove do ranking ao desconectar
                if (lastStats?.ProcessStat?.InstanceId != null)
                {
                    var removed = _rankingManager.Remove(lastStats.ProcessStat.InstanceId);
                    if (removed)
                        _logger.LogInformation("✅ Instance {Name} removed from ranking.", lastStats.ProcessStat.InstanceName);
                    else
                        _logger.LogWarning("⚠️ Failed to remove: {Id} not found in ranking.", lastStats.ProcessStat.InstanceId);
                }
                // OBS: se quiser remover mesmo quando só houve HELLO (sem métricas),
                // adapte TryProcessHelloFromPi para retornar o InstanceId (out param) e
                // guarde em uma variável para usar aqui.
            }
        }


        /// <summary>
        /// Processa HELLO do PI. Se a mensagem contém campos de HELLO, registra endpoint público
        /// e retorna true (já tratou). Se não for HELLO, retorna false para o chamador tentar como estatística.
        /// </summary>
        private bool TryProcessHelloFromPi(string jsonMessage, TcpClient client)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonMessage);
                var root = doc.RootElement;

                if (!root.TryGetProperty("InstanceId", out var pId))
                    return false;

                var id = pId.GetString();
                if (string.IsNullOrWhiteSpace(id))
                    return false;

                string? host = root.TryGetProperty("PublicHost", out var pHost) ? pHost.GetString() : null;

                int port = 0;
                if (root.TryGetProperty("PublicPort", out var pPort) && pPort.ValueKind == JsonValueKind.Number)
                    port = pPort.GetInt32();
                else if (root.TryGetProperty("GeneralPort", out var gPort) && gPort.ValueKind == JsonValueKind.Number)
                    port = gPort.GetInt32();

                bool useHttps =
                    root.TryGetProperty("UseHttps", out var pHttps) &&
                    (pHttps.ValueKind == JsonValueKind.True ||
                     (pHttps.ValueKind == JsonValueKind.String && bool.TryParse(pHttps.GetString(), out var b) && b));

                if (port <= 0)
                {
                    _logger.LogWarning("[HANDSHAKE TM] HELLO from {Id} without public port. Ignoring HELLO.", id);
                    return true; // era HELLO, mas inválido → não tente parsear como estatística
                }

                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "127.0.0.1";
                var chosenHost = string.IsNullOrWhiteSpace(host) ? remoteIp : host!.Trim();
                var scheme = useHttps ? "https" : "http";
                var baseUrl = $"{scheme}://{chosenHost}:{port}";

                _rankingManager.RegisterPublicEndpoint(id!, baseUrl);
                _rankingManager.TouchAlive(id!);

                _logger.LogInformation("[HANDSHAKE TM] Registered public endpoint of {Id} → {Url}", id, baseUrl);
                return true;
            }
            catch
            {
                return false; // não é JSON válido
            }
        }

        /// <summary>
        /// Extrai 0..N objetos JSON completos do acumulador (balanceamento de chaves), mesmo sem delimitador.
        /// Respeita strings e escapes.
        /// </summary>
        private static IEnumerable<string> ExtractCompleteJsonObjects(StringBuilder acc)
        {
            var list = new List<string>();
            int depth = 0;
            bool inString = false;
            bool escape = false;
            int startIdx = -1;

            for (int i = 0; i < acc.Length; i++)
            {
                var c = acc[i];

                if (inString)
                {
                    if (escape) { escape = false; }
                    else if (c == '\\') { escape = true; }
                    else if (c == '"') { inString = false; }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    if (depth == 0 && startIdx == -1)
                        startIdx = (startIdx == -1) ? i : startIdx;
                }
                else if (c == '{')
                {
                    if (depth == 0) startIdx = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && startIdx >= 0)
                    {
                        var len = (i - startIdx) + 1;
                        var json = acc.ToString(startIdx, len);
                        list.Add(json);

                        acc.Remove(0, i + 1);
                        i = -1;
                        startIdx = -1;
                    }
                }
            }

            return list;
        }

        // ========== Stream de Observabilidade (NDJSON) ==========
        private void StartObserverStream(CancellationToken ct)
        {
            try
            {
                _obsListener = new TcpListener(IPAddress.Any, _streamPort);
                _obsListener.Start();
                _logger.LogInformation("[STREAM] Observability listening on port {Port} (NDJSON).", _streamPort);

                // Accept loop
                _ = Task.Run(() => AcceptObserversAsync(ct), ct);

                // Publisher loop
                _ = Task.Run(() => PublishSnapshotsLoopAsync(ct), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STREAM] Failed to start observability listener on port {Port}", _streamPort);
            }
        }

        private async Task AcceptObserversAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _obsListener!.AcceptTcpClientAsync(ct);
                    _ = HandleObserverAsync(client, ct);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[STREAM] Accept observers canceled.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[STREAM] Error accepting observer connection");
                }
            }
        }

        private async Task HandleObserverAsync(TcpClient client, CancellationToken ct)
        {
            StreamWriter? w = null;
            try
            {
                var stream = client.GetStream();
                w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

                var id = Guid.NewGuid();
                _observers[id] = w;

                // hello_ack
                await w.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "hello_ack",
                    server = "Anakim",
                    role = "TM",
                    ts = DateTime.UtcNow
                }));

                _logger.LogInformation("[STREAM] Observer connected: {Remote}", client.Client.RemoteEndPoint);

                // heartbeats
                _ = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), ct);
                        try
                        {
                            await w.WriteLineAsync(JsonSerializer.Serialize(new { type = "heartbeat", ts = DateTime.UtcNow }));
                        }
                        catch { break; }
                    }
                }, ct);

                // mantém aberto até o cliente fechar
                var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;
                    // opcional: tratar "ping" etc.
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[STREAM] Error handling observer");
            }
            finally
            {
                try
                {
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
                _logger.LogInformation("[STREAM] Observer disconnected.");
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

                    // TM + PIs (+ agregados de AHs por PI), pronto para NDJSON
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
                    _logger.LogWarning(ex, "[STREAM] publish error");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); } catch { }
            }
        }

        // Exposes the current best-ranked Proxy Instance
        public NodeStatistics? GetBestProxyInstance() => _rankingManager.GetBestInstance();
    }
}
