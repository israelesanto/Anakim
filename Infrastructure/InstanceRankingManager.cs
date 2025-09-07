using AnakimOrchestrator.Infrastructure;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AnakimOrchestrator.Infrastructure
{
    // Manages ranking and availability of running instances (e.g., ProxyInstances or ApplicationHandlers)
    public class InstanceRankingManager
    {
        // ---------------- Internals ----------------

        // Internal class to store statistics along with last update timestamp
        private class TimedStat
        {
            public NodeStatistics? Statistics { get; set; }
            public DateTime LastUpdateUtc { get; set; }
        }

        // Thread-safe dictionary to store instance stats indexed by InstanceId
        private readonly ConcurrentDictionary<string, TimedStat> _instances = new();

        // Mapa InstanceId -> PublicBaseUrl (ex.: https://host:port)
        private readonly ConcurrentDictionary<string, string> _publicEndpoints = new();

        // Time after which a node's data is considered stale (↑ 60s para reduzir falsos negativos)
        private readonly TimeSpan _expirationTime = TimeSpan.FromSeconds(60);

        // ---------------- Public API ----------------

        // Atualiza/insere métricas do nó
        public void Update(NodeStatistics stats)
        {
            if (stats?.ProcessStat?.InstanceId == null)
                return;

            _instances[stats.ProcessStat.InstanceId] = new TimedStat
            {
                Statistics = stats,
                LastUpdateUtc = DateTime.UtcNow
            };
        }

        // Mantém o nó "vivo" mesmo sem métricas (use no HELLO)
        public void TouchAlive(string instanceId, NodeStatistics? optionalStats = null)
        {
            _instances.AddOrUpdate(
                instanceId,
                id => new TimedStat { Statistics = optionalStats, LastUpdateUtc = DateTime.UtcNow },
                (id, old) =>
                {
                    old.LastUpdateUtc = DateTime.UtcNow;
                    if (optionalStats != null) old.Statistics = optionalStats;
                    return old;
                });
        }

        // Remove nó do ranking e do cache de endpoint
        public bool Remove(string instanceId)
        {
            var removed = _instances.TryRemove(instanceId, out _);
            _publicEndpoints.TryRemove(instanceId, out _); // mantém coesão
            return removed;
        }

        // Melhor nó por consumo de memória (entre "frescos")
        public NodeStatistics? GetBestInstance()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime)
                .Select(x => x.Statistics)
                .OfType<NodeStatistics>()
                .Where(x => x.ProcessStat != null)
                .OrderBy(x =>
                    (x!.ProcessStat!.CpuUsage * 0.0) +
                    (x.ProcessStat.PrivateMemoryMB * 1.0))
                .FirstOrDefault();
        }

        // Todos os nós "frescos"
        public IReadOnlyCollection<NodeStatistics> GetAll()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime)
                .Select(x => x.Statistics)
                .Where(x => x != null)
                .Cast<NodeStatistics>()
                .ToList()
                .AsReadOnly();
        }

        // Para FailoverManager: ordenação atual
        public List<NodeStatistics> GetRankedInstances()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime)
                .Select(x => x.Statistics)
                .Where(x => x != null && x!.ProcessStat != null)
                .OrderBy(x =>
                    (x!.ProcessStat!.CpuUsage * 0.0) +
                    (x.ProcessStat.PrivateMemoryMB * 1.0))
                .ToList()!;
        }

        // ---------------- Registro/consulta de endpoints públicos ----------------

        /// <summary>Registra/atualiza a URL pública (ex.: "AH07" -> "https://localhost:8016").</summary>
        public void RegisterPublicEndpoint(string instanceId, string publicBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(publicBaseUrl))
                return;

            _publicEndpoints[instanceId] = publicBaseUrl.Trim();
        }

        /// <summary>Obtém a URL pública registrada, se houver.</summary>
        public string? GetPublicEndpoint(string instanceId)
        {
            return _publicEndpoints.TryGetValue(instanceId, out var url) ? url : null;
        }

        // ---------------- Suporte ao TryGetRankedUrl (reflexão) ----------------

        public string? GetBestApplicationHandlerUrl() => GetBestUrlByRole(isAh: true);
        public string? GetBestProxyInstanceUrl() => GetBestUrlByRole(isAh: false);

        public string? TopApplicationHandlerEndpoint => GetBestApplicationHandlerUrl();
        public string? TopProxyInstanceEndpoint => GetBestProxyInstanceUrl();

        // ---------------- Helpers internos ----------------

        private string? GetBestUrlByRole(bool isAh)
        {
            var now = DateTime.UtcNow;

            // 0) Snapshot para log/diagnóstico
            var snapshot = _instances.ToArray();
            Logger.LogInfo($"[RANK] _instances.Count = {snapshot.Length}, isAh={isAh}");

            // 1) Filtra por frescor (sem papel)
            var fresh = snapshot
                .Where(kv => now - kv.Value.LastUpdateUtc <= _expirationTime)
                .ToList();

            // Log de cada item
            foreach (var kv in snapshot)
            {
                var ageTs = now - kv.Value.LastUpdateUtc;
                var ageSec = ageTs.TotalSeconds;
                var isFresh = ageTs <= _expirationTime;
                var roleMatch = MatchesRoleHeuristic(kv.Key, kv.Value.Statistics, isAh);

                Logger.LogInfo($"[RANK] {kv.Key} age={ageSec:F1}s fresh={isFresh} roleMatch={roleMatch}");
            }

            // 1.1) Se nada fresco, usa o mais recente que dê pra resolver URL
            if (fresh.Count == 0)
            {
                Logger.LogWarning("[RANK] Nenhuma instância 'fresh'. Vou tentar fallback pelo mais recente (ignorando expiração).");
                var lax = snapshot.OrderByDescending(kv => kv.Value.LastUpdateUtc);
                foreach (var kv in lax)
                {
                    var url0 = ResolveUrlForInstance(kv.Key, kv.Value);
                    if (!string.IsNullOrWhiteSpace(url0))
                    {
                        Logger.LogInfo($"[RANK] Fallback (ignora expiração) escolheu {kv.Key} -> {url0}");
                        return url0;
                    }
                }
                return null;
            }

            // 2) Aplica papel (AH/PI). Se ninguém casar, usa os frescos mesmo
            var roleCandidates = fresh
                .Where(kv => MatchesRoleHeuristic(kv.Key, kv.Value.Statistics, isAh))
                .ToList();

            var pool = roleCandidates.Count > 0 ? roleCandidates : fresh;
            if (roleCandidates.Count == 0)
                Logger.LogWarning("[RANK] Nenhuma instância casou com o papel (AH/PI). Usando 'fresh' sem filtrar papel.");

            // 3) Ordena por 'menos memória' e pega a 1ª cuja URL seja resolvível
            var ordered = pool.OrderBy(kv =>
            {
                var s = kv.Value.Statistics?.ProcessStat;
                return s == null ? double.MaxValue
                                 : (s.CpuUsage * 0.0) + (s.PrivateMemoryMB * 1.0);
            });

            foreach (var kv in ordered)
            {
                var url = ResolveUrlForInstance(kv.Key, kv.Value);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    Logger.LogInfo($"[RANK] Escolhido {kv.Key} -> {url}");
                    return url;
                }
            }

            Logger.LogWarning("[RANK] Nenhuma URL pôde ser resolvida nos candidatos.");
            return null;
        }

        private string? ResolveUrlForInstance(string instanceId, TimedStat ts)
        {
            if (_publicEndpoints.TryGetValue(instanceId, out var cached))
                return cached;

            if (TryBuildPublicUrlFromStats(ts.Statistics, out var built))
            {
                _publicEndpoints[instanceId] = built!;
                return built;
            }

            return null;
        }

        /// <summary>
        /// Monta "scheme://host:port" a partir de NodeStatistics por reflexão.
        /// Procura por:
        /// - PublicPort/GeneralPort no ProcessStat OU no root (int/long/string)
        /// - PublicHost/Host/Hostname/Address no ProcessStat OU PublicHost/SenderIP no root
        /// - UseHttps no ProcessStat OU no root
        /// - PortsInfo (Port/PortNumber, Protocol, Name) como fallback
        /// </summary>
        private static bool TryBuildPublicUrlFromStats(NodeStatistics? ns, out string? url)
        {
            url = null;
            if (ns == null) return false;
            var ps = ns.ProcessStat;
            if (ps == null) return false;

            // helpers
            static object? GetProp(object obj, params string[] names)
            {
                var t = obj.GetType();
                foreach (var n in names)
                {
                    var p = t.GetProperty(n,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (p != null) return p.GetValue(obj);
                }
                return null;
            }
            static int ParsePort(object? o)
            {
                if (o == null) return 0;
                return o switch
                {
                    int i => i,
                    long l => (int)l,
                    string s when int.TryParse(s, out var v) => v,
                    _ => 0
                };
            }

            // HOST (ps ou root)
            string? host =
                (GetProp(ps, "PublicHost") as string) ??
                (GetProp(ns, "PublicHost") as string) ??
                (GetProp(ns, "SenderIP") as string) ??
                (GetProp(ps, "Host") as string) ??
                (GetProp(ps, "Hostname") as string) ??
                (GetProp(ps, "Address") as string);

            // PORT (ps ou root): PublicPort / GeneralPort
            int port =
                ParsePort(GetProp(ps, "PublicPort") ?? GetProp(ns, "PublicPort"));
            if (port <= 0)
                port = ParsePort(GetProp(ps, "GeneralPort") ?? GetProp(ns, "GeneralPort"));

            // HTTPS flag (ps ou root)
            bool useHttps = false;
            var useHttpsObj = GetProp(ps, "UseHttps") ?? GetProp(ns, "UseHttps");
            if (useHttpsObj is bool b) useHttps = b;

            // Se ainda sem porta, tente PortsInfo
            if (port <= 0)
            {
                var portsInfo = GetProp(ps, "PortsInfo") as IEnumerable
                                ?? GetProp(ns, "PortsInfo") as IEnumerable;

                if (portsInfo != null)
                {
                    // preferir itens "https/http/general/public"
                    foreach (var entry in portsInfo)
                    {
                        string? proto = (GetProp(entry!, "Protocol") as string);
                        string? name = (GetProp(entry!, "Name") as string);
                        int pnum = ParsePort(GetProp(entry!, "PortNumber") ?? GetProp(entry!, "Port"));
                        if (pnum <= 0) continue;

                        var isHttps = string.Equals(proto, "https", StringComparison.OrdinalIgnoreCase);
                        var isHttp = string.Equals(proto, "http", StringComparison.OrdinalIgnoreCase);
                        var nameHit = (name ?? "").IndexOf("http", StringComparison.OrdinalIgnoreCase) >= 0
                                   || (name ?? "").IndexOf("general", StringComparison.OrdinalIgnoreCase) >= 0
                                   || (name ?? "").IndexOf("public", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isHttps || isHttp || nameHit)
                        {
                            port = pnum;
                            if (isHttps) useHttps = true;
                            break;
                        }
                    }
                    // ainda nada? pega o primeiro válido
                    if (port <= 0)
                    {
                        foreach (var entry in portsInfo)
                        {
                            int pnum = ParsePort(GetProp(entry!, "PortNumber") ?? GetProp(entry!, "Port"));
                            if (pnum > 0)
                            {
                                port = pnum;
                                var proto = GetProp(entry!, "Protocol") as string;
                                if (string.Equals(proto, "https", StringComparison.OrdinalIgnoreCase))
                                    useHttps = true;
                                break;
                            }
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(host)) host = "localhost";
            if (port <= 0)
            {
                Logger.LogWarning("[RANK] Não foi possível resolver porta pública a partir das métricas (faltou PublicPort/GeneralPort/PortsInfo).");
                return false;
            }

            var scheme = useHttps ? "https" : "http";
            url = $"{scheme}://{host}:{port}";
            Logger.LogInfo($"[RANK] URL derivada das métricas: {url}");
            return true;
        }

        /// <summary>Heurística simples para diferenciar AH vs PI pelo InstanceId.</summary>
        private static bool MatchesRoleHeuristic(string instanceId, NodeStatistics? stats, bool wantAh)
        {
            var id = instanceId ?? string.Empty;
            var isAhId = id.StartsWith("AH", StringComparison.OrdinalIgnoreCase)
                         || id.Contains("ApplicationHandler", StringComparison.OrdinalIgnoreCase)
                         || id.Contains("Application Handler", StringComparison.OrdinalIgnoreCase);

            var isPiId = id.StartsWith("PI", StringComparison.OrdinalIgnoreCase)
                         || id.Contains("ProxyInstance", StringComparison.OrdinalIgnoreCase)
                         || id.Contains("Proxy Instance", StringComparison.OrdinalIgnoreCase);

            if (wantAh && isAhId) return true;
            if (!wantAh && isPiId) return true;

            // Se não for conclusivo, aceite ambos (evita “sumir” instâncias por nomenclatura diferente)
            return true;
        }
    }
}
