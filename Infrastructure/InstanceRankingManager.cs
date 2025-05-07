using Anakim.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Anakim.Infrastructure
{
    public class InstanceRankingManager
    {
        private class TimedStat
        {
            public NodeStatistics Statistics { get; set; }
            public DateTime LastUpdateUtc { get; set; }
        }

        private readonly ConcurrentDictionary<string, TimedStat> _instances = new();
        private readonly TimeSpan _expirationTime = TimeSpan.FromSeconds(15);

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

        public bool Remove(string instanceId)
        {
            return _instances.TryRemove(instanceId, out _);
        }

        public NodeStatistics? GetBestInstance()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime)
                .Select(x => x.Statistics)
                .OrderBy(x =>
                    (x.ProcessStat.CpuUsage * 0.0) +
                    (x.ProcessStat.PrivateMemoryMB * 1.0))
                .FirstOrDefault();
        }

        public IReadOnlyCollection<NodeStatistics> GetAll()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime)
                .Select(x => x.Statistics)
                .ToList()
                .AsReadOnly();
        }

        // ✅ Novo método para integração com FailoverManager
        public List<NodeStatistics> GetRankedInstances()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime)
                .Select(x => x.Statistics)
                .OrderBy(x =>
                    (x.ProcessStat.CpuUsage * 0.0) +
                    (x.ProcessStat.PrivateMemoryMB * 1.0))
                .ToList();
        }
    }
}
