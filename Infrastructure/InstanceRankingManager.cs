using AccessPoint.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Anakim.Infrastructure
{
    public class InstanceRankingManager
    {
        private readonly ConcurrentDictionary<string, NodeStatistics> _instances = new();

        public void Update(NodeStatistics stats)
        {
            if (stats?.ProcessStat?.InstanceId == null)
                return;

            _instances[stats.ProcessStat.InstanceId] = stats;
        }

        public NodeStatistics? GetBestInstance()
        {
            return _instances.Values
                .Where(x => x != null)
                .OrderBy(x => x.ProcessStat.CpuUsage)
                .ThenBy(x => x.ProcessStat.MemoryUsageMB)
                .ThenBy(x => x.ProcessStat.ActiveThreads)
                .FirstOrDefault();
        }

        public IReadOnlyCollection<NodeStatistics> GetAll()
            => _instances.Values.ToList().AsReadOnly();
    }

}
