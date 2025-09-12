using System.Runtime.InteropServices;

namespace AnakimOrchestrator.Helpers
{
    public static class SystemMemoryInfo
    {
        public static (double totalMb, double freeMb) Snapshot()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MEMORYSTATUSEX st = new() { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref st))
                {
                    double total = st.ullTotalPhys / (1024.0 * 1024.0);
                    double free = st.ullAvailPhys / (1024.0 * 1024.0);
                    return (total, free);
                }
                return (0, 0);
            }
            else
            {
                // Linux
                try
                {
                    var dict = System.IO.File.ReadAllLines("/proc/meminfo")
                        .Select(l => l.Split(':'))
                        .ToDictionary(a => a[0], a => a[1].Trim());
                    double kb(string key) => double.Parse(dict[key].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
                    // MemAvailable ≈ memória livre utilizável
                    var totalKb = kb("MemTotal");
                    var availKb = dict.ContainsKey("MemAvailable") ? kb("MemAvailable") : kb("MemFree");
                    return (totalKb / 1024.0, availKb / 1024.0);
                }
                catch { return (0, 0); }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }

}
