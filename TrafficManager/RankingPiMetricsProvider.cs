// AnakimOrchestrator/TrafficManager/RankingPiMetricsProvider.cs
using System.Collections.Generic;
using System.Linq;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.TrafficManager
{
    public sealed class RankingPiMetricsProvider : IPiMetricsProvider
    {
        private readonly InstanceRankingManager _ranking;

        public RankingPiMetricsProvider(InstanceRankingManager ranking)
        {
            _ranking = ranking;
        }

        public IReadOnlyList<PiSnapshot> GetAll()
        {
            var nodes = _ranking.GetAllInstances(); // método adicionado acima
            if (nodes == null || nodes.Count == 0) return new List<PiSnapshot>();

            var list = new List<PiSnapshot>(nodes.Count);
            foreach (var n in nodes)
            {
                var ps = n.ProcessStat;
                if (ps == null) continue;

                // Monte os campos disponíveis; ajuste se você tiver req/err/lat reais
                list.Add(new PiSnapshot
                {
                    Id = ps.InstanceId ?? "",
                    Name = ps.InstanceName ?? "",
                    Up = true,                                // você pode derivar de um campo de health se tiver
                    Score = 0,                                // se tiver um score no ranking, preencha aqui
                    CpuPct = ps.CpuUsage,                     // já vem em %
                    MemMb = ps.MemoryUsageMB,                 // RSS do processo PI em MB
                    ReqRate = 0,                              // preencha se tiver fonte
                    ErrRate = 0,
                    P50 = 0,
                    P95 = 0,
                    P99 = 0,
                    AhAgg = null                              // se o TM mantiver agregados por PI, preencha
                });
            }
            return list;
        }
    }
}
