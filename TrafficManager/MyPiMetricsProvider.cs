using System;
using System.Collections.Generic;

namespace AnakimOrchestrator.TrafficManager
{
    public sealed class MyPiMetricsProvider : IPiMetricsProvider
    {
        public IReadOnlyList<PiSnapshot> GetAll()
        {
            // TODO: ligar no seu registry/monitor real de PIs
            return Array.Empty<PiSnapshot>();
        }
    }
}
