using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AnakimOrchestrator.Helpers
{
    public sealed class SystemCpuMeter : IDisposable
    {
        // Windows
        private PerformanceCounter? _winTotal;
        // Linux
        private ulong _prevIdle, _prevTotal;
        private bool _first = true;

        public SystemCpuMeter()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _winTotal = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _ = _winTotal.NextValue(); // prime
            }
            else
            {
                // Linux: primeira leitura para base
                ReadLinux(out _prevIdle, out _prevTotal);
            }
        }

        public double NextPercent()
        {
            if (_winTotal is not null)
            {
                // Windows retorna já em %
                return Math.Round(_winTotal.NextValue(), 2);
            }

            // Linux
            if (!ReadLinux(out var idle, out var total)) return 0;

            if (_first) { _first = false; _prevIdle = idle; _prevTotal = total; return 0; }

            var idleDelta = idle - _prevIdle;
            var totalDelta = total - _prevTotal;
            _prevIdle = idle; _prevTotal = total;

            if (totalDelta == 0) return 0;
            var usage = (1.0 - (double)idleDelta / totalDelta) * 100.0;
            return Math.Round(Math.Max(0, Math.Min(100, usage)), 2);
        }

        private static bool ReadLinux(out ulong idle, out ulong total)
        {
            idle = 0; total = 0;
            try
            {
                var line = System.IO.File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("cpu "));
                if (line is null) return false;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // cpu user nice system idle iowait irq softirq steal guest guest_nice
                // índices:    1    2     3     4    5      6   7       8     9     10
                var vals = parts.Skip(1).Select(ulong.Parse).ToArray();
                idle = vals[3]; // idle
                total = (ulong)vals.Sum(v => (decimal)v);
                return true;
            }
            catch { return false; }
        }

        public void Dispose() => _winTotal?.Dispose();
    }

}
