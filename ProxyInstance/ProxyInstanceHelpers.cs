using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.ProxyInstance
{
    static class ProxyInstanceHelpers
    {
        private static int _ahIndex = 0;

        public static Task<string> ResolveBestApplicationHandlerUrlAsync(HttpContext ctx, IConfiguration cfg)
        {
            // 1) Variáveis de ambiente
            var list = RoutingUtils.ReadEnvList("AH_URLS");
            if (list.Count > 0)
            {
                var chosen = RoutingUtils.PickRoundRobin(list, ref _ahIndex);
                return Task.FromResult(RoutingUtils.ComposeTargetUrl(chosen, ctx));
            }

            // 2) Ranking dinâmico
            var ranked = RoutingUtils.TryGetRankedUrl(ctx, preferAh: true);
            if (!string.IsNullOrWhiteSpace(ranked))
                return Task.FromResult(RoutingUtils.ComposeTargetUrl(ranked!, ctx));

            // 3) Fallback: localhost:8001
            var scheme = cfg.GetValue<bool>("UseHttps") ? "https" : "http";
            var fallback = $"{scheme}://localhost:8001";
            return Task.FromResult(RoutingUtils.ComposeTargetUrl(fallback, ctx));
        }
    }
}
