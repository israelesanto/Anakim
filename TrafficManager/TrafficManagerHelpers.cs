using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.TrafficManager
{
    static class TrafficManagerHelpers
    {
        private static int _piIndex = 0;

        public static Task<string> ResolveBestProxyInstanceUrlAsync(HttpContext ctx, IConfiguration cfg)
        {
            // 1) Variáveis de ambiente (mantém appsettings.json limpo)
            var list = RoutingUtils.ReadEnvList("PI_URLS");
            if (list.Count > 0)
            {
                var chosen = RoutingUtils.PickRoundRobin(list, ref _piIndex);
                return Task.FromResult(RoutingUtils.ComposeTargetUrl(chosen, ctx));
            }

            // 2) Ranking dinâmico (se expuser URL)
            var ranked = RoutingUtils.TryGetRankedUrl(ctx, preferAh: false);
            if (!string.IsNullOrWhiteSpace(ranked))
                return Task.FromResult(RoutingUtils.ComposeTargetUrl(ranked!, ctx));

            // 3) Fallback: localhost:7001 (respeitando UseHttps)
            var scheme = cfg.GetValue<bool>("UseHttps") ? "https" : "http";
            var fallback = $"{scheme}://localhost:7001";
            return Task.FromResult(RoutingUtils.ComposeTargetUrl(fallback, ctx));
        }
    }
}
