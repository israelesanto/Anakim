using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnakimOrchestrator.Infrastructure
{
    public static class RoutingUtils
    {
        public static List<string> ReadEnvList(string key)
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                      .Select(s => s.Trim())
                      .Where(s => s.Length > 0)
                      .ToList();
        }

        public static string PickRoundRobin(List<string> list, ref int index)
        {
            var i = Interlocked.Increment(ref index);
            return list[(i - 1) % list.Count];
        }

        public static string ComposeTargetUrl(string baseUrl, HttpContext ctx)
        {
            var path = ctx.Request.Path.HasValue ? ctx.Request.Path.Value : string.Empty;
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : string.Empty;
            if (baseUrl.EndsWith("/")) baseUrl = baseUrl.TrimEnd('/');
            return $"{baseUrl}{path}{query}";
        }

        public static string? TryGetRankedUrl(HttpContext ctx, bool preferAh)
        {
            var sp = ctx.RequestServices;
            var mgr = sp.GetService(typeof(InstanceRankingManager));
            if (mgr is null) return null;

            var t = mgr.GetType();

            var methodName = preferAh ? "GetBestApplicationHandlerUrl" : "GetBestProxyInstanceUrl";
            var m = t.GetMethod(methodName, Type.EmptyTypes);
            if (m != null && m.ReturnType == typeof(string))
            {
                var url = m.Invoke(mgr, null) as string;
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }

            var propName = preferAh ? "TopApplicationHandlerEndpoint" : "TopProxyInstanceEndpoint";
            var p = t.GetProperty(propName);
            if (p != null && p.PropertyType == typeof(string))
            {
                var url = p.GetValue(mgr) as string;
                if (!string.IsNullOrWhiteSpace(url)) return url;
            }

            return null;
        }
    }
}

