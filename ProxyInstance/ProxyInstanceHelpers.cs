using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using AnakimOrchestrator.Infrastructure;

namespace AnakimOrchestrator.ProxyInstance
{
    static class ProxyInstanceHelpers
    {
        // Agora permite null para sinalizar "sem AH disponível"
        public static async Task<string?> ResolveBestApplicationHandlerUrlAsync(HttpContext ctx, IConfiguration cfg)
        {
            // Somente ranking dinâmico (descoberto via handshake)
            var ranked = RoutingUtils.TryGetRankedUrl(ctx, preferAh: true);
            if (!string.IsNullOrWhiteSpace(ranked))
            {
                return RoutingUtils.ComposeTargetUrl(ranked!, ctx);
            }

            // Sem AH disponível → 503
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await ctx.Response.WriteAsync("No Application Handler available");
            return null;
        }
    }
}
