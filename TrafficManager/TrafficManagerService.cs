using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.TrafficManager
{
    // Service that runs in Traffic Manager mode (Mode = 1) and listens for statistics from Proxy Instances
    public class TrafficManagerService : IHostedService
    {
        private readonly IConfiguration _configuration;
        private TcpListener? _listener;
        private readonly int _port;
        private readonly InstanceRankingManager _rankingManager;

        // Constructor validates configuration and sets up port and ranking manager
        public TrafficManagerService(IConfiguration configuration, InstanceRankingManager rankingManager)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _rankingManager = rankingManager ?? throw new ArgumentNullException(nameof(rankingManager));

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
                catch (OperationCanceledException) { /* shutdown */ }
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

            var remote = client?.Client?.RemoteEndPoint?.ToString() ?? "unknown";
            Logger.LogInfo($"Connection established with {remote}");

            try
            {
                if (client is null)
                {
                    Logger.LogError("TcpClient é nulo. Encerrando execução.");
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
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        Logger.LogWarning($"Client {remote} disconnected.");
                        break;
                    }

                    acc.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                    // Podem ter chegado 0..N JSONs completos no acumulador
                    foreach (var json in ExtractCompleteJsonObjects(acc))
                    {
                        var payload = json.Trim();
                        if (string.IsNullOrWhiteSpace(payload))
                            continue;

                        // 1) Tenta HELLO do PI (idempotente). Se for HELLO, já tratou e segue.
                        if (TryProcessHelloFromPi(payload, client))
                            continue;

                        // 2) Tenta estatísticas
                        try
                        {
                            var stats = JsonSerializer.Deserialize<NodeStatistics>(payload);
                            if (stats?.ProcessStat != null)
                            {
                                lastStats = stats;
                                _rankingManager.Update(stats);

                                Logger.LogInfo($"Statistics updated for {stats.ProcessStat.InstanceName} [{stats.ProcessStat.InstanceId}]");

                                var best = _rankingManager.GetBestInstance();
                                if (best?.ProcessStat != null)
                                {
                                    Logger.LogInfo($"🟢 Top ranked: {best.ProcessStat.InstanceName} | CPU: {best.ProcessStat.CpuUsage} | Memory: {best.ProcessStat.PrivateMemoryMB}MB");
                                }
                            }
                            else
                            {
                                // Não derruba a conexão; pode ser outra mensagem futura (ex.: ping/ack/hello reemitido)
                                Logger.LogInfo("Statistics or its ProcessStat is null (ignorado).");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning($"[TM] Falha ao parsear mensagem como NodeStatistics: {ex.Message}");
                            // segue o loop; não fecha o socket
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error handling client: {ex.Message}");
            }
            finally
            {
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

                // Se não houver InstanceId, não é HELLO
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

                bool useHttps = root.TryGetProperty("UseHttps", out var pHttps) &&
                                (pHttps.ValueKind == JsonValueKind.True ||
                                 (pHttps.ValueKind == JsonValueKind.String && bool.TryParse(pHttps.GetString(), out var b) && b));

                // HELLO inválido? Não trate como estatística; só ignore.
                if (port <= 0)
                {
                    Logger.LogWarning($"[HANDSHAKE TM] HELLO from {id} without public port. Ignoring HELLO.");
                    return true; // era HELLO, mas inválido — evita cair no parser de métricas
                }

                // Se o host não vier, usa o IP remoto da conexão
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?.Address?.ToString() ?? "127.0.0.1";
                var chosenHost = string.IsNullOrWhiteSpace(host) ? remoteIp : host!.Trim();
                var scheme = useHttps ? "https" : "http";
                var baseUrl = $"{scheme}://{chosenHost}:{port}";

                _rankingManager.RegisterPublicEndpoint(id!, baseUrl);
                _rankingManager.TouchAlive(id!);

                Logger.LogInfo($"[HANDSHAKE TM] Registered public endpoint of {id} → {baseUrl}");
                return true;
            }
            catch
            {
                return false; // não é JSON válido → deixa o chamador tentar como NodeStatistics
            }
        }

        /// <summary>
        /// Extrai 0..N objetos JSON completos do acumulador (balanceamento de chaves), mesmo sem delimitador.
        /// Usa uma heurística simples que respeita strings e escapes.
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
                    if (escape)
                    {
                        escape = false;
                    }
                    else if (c == '\\')
                    {
                        escape = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
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
                    if (depth == 0)
                        startIdx = i;
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

                        // Remove do acumulador tudo até i (inclusive)
                        acc.Remove(0, i + 1);
                        // Reinicia varredura no novo buffer
                        i = -1;
                        startIdx = -1;
                    }
                }
            }

            return list;
        }

        // Exposes the current best-ranked Proxy Instance
        public NodeStatistics? GetBestProxyInstance()
        {
            return _rankingManager.GetBestInstance();
        }
    }
}
