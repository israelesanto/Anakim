using System;
using System.Collections.Generic;
using System.Linq;
using AnakimOrchestrator.Infrastructure; // NodeStatisticsService, NodeStatistics, ProxySettings
using Microsoft.Extensions.Configuration;

namespace AnakimOrchestrator.ProxyInstance
{
    /// <summary>
    /// Agregador de estatísticas do PI:
    /// - Usa INodeStatisticsService para coletar CPU/Mem do próprio processo e do host
    /// - Usa IAhMetricsProvider para trazer o estado dos AHs
    /// - Monta um payload pronto para serialização NDJSON
    /// </summary>
    public interface IProxyStatisticsAggregator
    {
        /// <param name="full">
        /// true = inclui lista detalhada de AHs; false = reusa a última lista “full” conhecida (snapshot leve)
        /// </param>
        object BuildSnapshot(bool full = false);
    }

    public sealed class ProxyStatisticsAggregator : IProxyStatisticsAggregator
    {
        private readonly INodeStatisticsService _nodeStats;
        private readonly IAhMetricsProvider _ahProvider;
        private readonly ProxySettings _proxySettings;

        // 🔒 Cache da última lista/resumo de AHs para snapshots leves
        private readonly object _cacheLock = new();
        private List<object> _lastAhList = new();
        private (int total, int up, int down) _lastAhSummary = (0, 0, 0);

        public ProxyStatisticsAggregator(
            INodeStatisticsService nodeStatisticsService,
            IAhMetricsProvider ahMetricsProvider,
            IConfiguration configuration)
        {
            _nodeStats = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
            _ahProvider = ahMetricsProvider ?? throw new ArgumentNullException(nameof(ahMetricsProvider));
            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>() ?? new ProxySettings();
        }

        public object BuildSnapshot(bool full = false)
        {
            // Estatísticas do próprio PI
            var node = _nodeStats.CollectStatistics();

            // Lista de AHs atual (do provider)
            var ahs = _ahProvider.GetAll() ?? Array.Empty<AhSnapshot>();
            var up = ahs.Count(a => a.Up);
            var down = ahs.Count - up;
            var total = ahs.Count;

            // Uptime do processo atual
            var uptimeSec = (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds;

            List<object> ahList;
            (int total, int up, int down) ahSummaryTuple;

            if (full)
            {
                // Monta lista detalhada e ATUALIZA o cache
                ahList = ahs.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    status = a.Up ? "up" : "down",
                    score = a.Score,
                    cpuPct = a.CpuPct,
                    memMb = a.MemMb,
                    reqRate = a.ReqRate,
                    errRate = a.ErrRate,
                    latencyMs = new { p50 = a.P50, p95 = a.P95, p99 = a.P99 },
                    queueDepth = a.QueueDepth,
                    health = a.Health
                }).Cast<object>().ToList();

                ahSummaryTuple = (total, up, down);

                lock (_cacheLock)
                {
                    _lastAhList = ahList;
                    _lastAhSummary = ahSummaryTuple;
                }
            }
            else
            {
                // Snapshot leve → reusa o ÚLTIMO full conhecido
                lock (_cacheLock)
                {
                    ahList = new List<object>(_lastAhList);
                    ahSummaryTuple = _lastAhSummary;
                }
            }

            // TODO: preencha traffic com suas métricas reais (conexões, taxas, latências) se já coletadas
            var payload = new
            {
                type = "metrics.pi",
                version = "1",
                ts = DateTime.UtcNow,
                role = "PI",
                instanceId = node.ProcessStat.InstanceId,
                instanceName = node.ProcessStat.InstanceName,
                uptimeSec,

                cpu = new
                {
                    procPct = node.ProcessStat.CpuUsage,   // % CPU do processo PI
                    sysPct = node.System.CpuUsage          // % CPU do host
                },
                mem = new
                {
                    procRssMb = node.ProcessStat.MemoryUsageMB,
                    gcHeapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2),
                    sysFreeMb = node.System.MemoryAvailableMB,
                    sysTotalMb = node.System.TotalMemoryMB
                },

                // placeholders: ajuste com suas métricas internas
                traffic = new
                {
                    activeConns = 0,
                    reqRate = 0.0,
                    errRate = 0.0,
                    latencyMs = new { p50 = 0.0, p95 = 0.0, p99 = 0.0 }
                },

                ahSummary = new
                {
                    total = ahSummaryTuple.total,
                    up = ahSummaryTuple.up,
                    down = ahSummaryTuple.down
                },

                ahs = ahList
            };

            return payload;
        }
    }

    /// <summary>
    /// Provedor de métricas dos AHs. Adapte para o seu "registry" atual.
    /// </summary>
    public interface IAhMetricsProvider
    {
        IReadOnlyList<AhSnapshot> GetAll();
    }

    /// <summary>
    /// Snapshot mínimo de um AH para publicação no stream.
    /// Adapte os campos conforme seu modelo interno.
    /// </summary>
    public sealed class AhSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Up { get; set; }
        public double Score { get; set; }

        // Recursos
        public double CpuPct { get; set; }
        public double MemMb { get; set; }

        // Tráfego
        public double ReqRate { get; set; }
        public double ErrRate { get; set; }

        // Latências (pXX)
        public double P50 { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }

        public int QueueDepth { get; set; }
        public string Health { get; set; } = "unknown";
    }
}
