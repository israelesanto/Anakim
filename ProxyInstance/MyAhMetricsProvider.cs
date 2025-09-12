using System;
using System.Collections.Generic;

namespace AnakimOrchestrator.ProxyInstance
{
    public sealed class MyAhMetricsProvider : IAhMetricsProvider
    {
        public IReadOnlyList<AhSnapshot> GetAll()
        {
            // TODO: ligar no seu registry/monitor real de AHs
            return Array.Empty<AhSnapshot>();
        }
    }
}
