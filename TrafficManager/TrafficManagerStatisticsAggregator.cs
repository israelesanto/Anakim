using System;
using System.Collections.Generic;
using System.Linq;
using AnakimOrchestrator.Infrastructure; // NodeStatisticsService, NodeStatistics, ProxySettings
using Microsoft.Extensions.Configuration;

namespace AnakimOrchestrator.TrafficManager
{
    public interface ITrafficManagerStatisticsAggregator
    {
        object BuildSnapshot(bool full = false);
    }

    public sealed class TrafficManagerStatisticsAggregator : ITrafficManagerStatisticsAggregator
    {
        private readonly INodeStatisticsService _nodeStats;
        private readonly IPiMetricsProvider _piProvider;
        private readonly ProxySettings _proxySettings;

        // 🔒 Cache da última lista e resumo de PIs para snapshots "leves"
        private readonly object _cacheLock = new();
        private List<object> _lastPiList = new();
        private (int total, int up, int down) _lastPiSummary = (0, 0, 0);

        public TrafficManagerStatisticsAggregator(
            INodeStatisticsService nodeStatisticsService,
            IPiMetricsProvider piMetricsProvider,
            IConfiguration configuration)
        {
            _nodeStats = nodeStatisticsService ?? throw new ArgumentNullException(nameof(nodeStatisticsService));
            _piProvider = piMetricsProvider ?? throw new ArgumentNullException(nameof(piMetricsProvider));
            _proxySettings = configuration.GetSection("ProxySettings").Get<ProxySettings>() ?? new ProxySettings();
        }

        public object BuildSnapshot(bool full = false)
        {
            // Estatísticas do próprio TM
            var node = _nodeStats.CollectStatistics();

            // PIs atuais via provider (já “fresh” quando baseado no Ranking)
            var pis = _piProvider.GetAll() ?? Array.Empty<PiSnapshot>();
            var up = pis.Count(p => p.Up);
            var down = pis.Count - up;
            var total = pis.Count;

            // Uptime do processo atual
            var uptimeSec = (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds;

            List<object> piList;
            (int total, int up, int down) piSummaryTuple;

            if (full)
            {
                // Monta lista detalhada e ATUALIZA o cache
                piList = pis.Select(p => new
                {
                    id = p.Id,
                    name = p.Name,
                    status = p.Up ? "up" : "down",
                    score = p.Score,
                    cpuPct = p.CpuPct,
                    memMb = p.MemMb,
                    reqRate = p.ReqRate,
                    errRate = p.ErrRate,
                    latencyMs = new { p50 = p.P50, p95 = p.P95, p99 = p.P99 },
                    ahAgg = p.AhAgg is null ? null : new
                    {
                        total = p.AhAgg.Total,
                        up = p.AhAgg.Up,
                        down = p.AhAgg.Down,
                        reqRate = p.AhAgg.ReqRate,
                        errRate = p.AhAgg.ErrRate
                    }
                }).Cast<object>().ToList();

                piSummaryTuple = (total, up, down);

                lock (_cacheLock)
                {
                    _lastPiList = piList;                 // guarda a ÚLTIMA lista completa
                    _lastPiSummary = piSummaryTuple;      // e o ÚLTIMO resumo
                }
            }
            else
            {
                // Snapshot leve → reusa o ÚLTIMO full conhecido
                lock (_cacheLock)
                {
                    piList = new List<object>(_lastPiList);
                    piSummaryTuple = _lastPiSummary;
                }
            }

            var payload = new
            {
                type = "metrics.tm",
                version = "1",
                ts = DateTime.UtcNow,
                role = "TM",
                instanceId = node.ProcessStat.InstanceId,
                instanceName = node.ProcessStat.InstanceName,
                uptimeSec,

                cpu = new
                {
                    procPct = node.ProcessStat.CpuUsage,   // % CPU do processo TM
                    sysPct = node.System.CpuUsage         // % CPU do host
                },
                mem = new
                {
                    procRssMb = node.ProcessStat.MemoryUsageMB,
                    gcHeapMb = Math.Round(GC.GetTotalMemory(false) / 1048576.0, 2),
                    sysFreeMb = node.System.MemoryAvailableMB,
                    sysTotalMb = node.System.TotalMemoryMB
                },

                // ainda placeholders, se você não plugar métricas reais
                traffic = new
                {
                    activeConns = 0,
                    reqRate = 0.0,
                    errRate = 0.0,
                    latencyMs = new { p50 = 0.0, p95 = 0.0, p99 = 0.0 }
                },

                piSummary = new
                {
                    total = piSummaryTuple.total,
                    up = piSummaryTuple.up,
                    down = piSummaryTuple.down
                },

                pis = piList
            };

            return payload;
        }
    }

    public interface IPiMetricsProvider
    {
        IReadOnlyList<PiSnapshot> GetAll();
    }

    public sealed class PiSnapshot
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Up { get; set; }
        public double Score { get; set; }
        public double CpuPct { get; set; }
        public double MemMb { get; set; }
        public double ReqRate { get; set; }
        public double ErrRate { get; set; }
        public double P50 { get; set; }
        public double P95 { get; set; }
        public double P99 { get; set; }
        public AhAggregate? AhAgg { get; set; }
    }

    public sealed class AhAggregate
    {
        public int Total { get; set; }
        public int Up { get; set; }
        public int Down { get; set; }
        public double ReqRate { get; set; }
        public double ErrRate { get; set; }
    }
}
