using Anakim.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Anakim.Infrastructure
{
    // Manages ranking and availability of running instances (e.g., ProxyInstances or ApplicationHandlers)
    public class InstanceRankingManager
    {
        // Internal class to store statistics along with last update timestamp
        private class TimedStat
        {
            public NodeStatistics? Statistics { get; set; }
            public DateTime LastUpdateUtc { get; set; }
        }

        // Thread-safe dictionary to store instance stats indexed by InstanceId
        private readonly ConcurrentDictionary<string, TimedStat> _instances = new();

        // Time after which a node's data is considered stale
        private readonly TimeSpan _expirationTime = TimeSpan.FromSeconds(15);

        // Updates the statistics for a given instance or inserts if new
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

        // Removes a specific instance from the ranking
        public bool Remove(string instanceId)
        {
            return _instances.TryRemove(instanceId, out _);
        }

        // Returns the best ranked (least loaded) instance among active ones
        public NodeStatistics? GetBestInstance()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime) // Only recent updates
                .Select(x => x.Statistics)
                .OfType<NodeStatistics>()
                .Where(x => x.ProcessStat != null)
                .OrderBy(x =>
                    (x.ProcessStat!.CpuUsage * 0.0) +                     // CPU usage is currently ignored
                    (x.ProcessStat.PrivateMemoryMB * 1.0))              // Memory usage used for ranking
                .FirstOrDefault();
        }

        // Returns all currently active instances
        public IReadOnlyCollection<NodeStatistics> GetAll()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime) // Filter out stale stats
                .Select(x => x.Statistics)
                .Where(x => x != null)
                .Cast<NodeStatistics>()
                .ToList()
                .AsReadOnly();
        }

        // ✅ New method for integration with FailoverManager
        // Returns all active instances sorted by current ranking (lowest memory usage)
        public List<NodeStatistics> GetRankedInstances()
        {
            var now = DateTime.UtcNow;

            return _instances.Values
                .Where(x => now - x.LastUpdateUtc <= _expirationTime)
                .Select(x => x.Statistics)
                .Where(x => x != null && x.ProcessStat != null)
                .OrderBy(x =>
                    (x!.ProcessStat!.CpuUsage * 0.0) +
                    (x.ProcessStat.PrivateMemoryMB * 1.0))
                .ToList()!;
        }
    }
}
