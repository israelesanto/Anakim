using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using AnakimOrchestrator.Infrastructure;
using System.Linq;
using System.Threading.Tasks;

namespace AnakimOrchestrator.ProxyInstance
{
    // Middleware that intercepts HTTP requests and forwards them to the best available Application Handler (AH) using failover
    public class RedirectToBestAHMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;
        private readonly IConfiguration _configuration;

        // Constructor receives dependencies via DI
        public RedirectToBestAHMiddleware(
            RequestDelegate next,
            InstanceRankingManager rankingManager,
            FailoverManager failoverManager,
            IConfiguration configuration)
        {
            _next = next;
            _rankingManager = rankingManager;
            _failoverManager = failoverManager;
            _configuration = configuration;
        }

        // Middleware execution logic
        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503; // Service unavailable
                await context.Response.WriteAsync("No Application Handler available.");
                return;
            }

            // Reads the protocol from configuration
            bool useHttps = _configuration.GetValue<bool>("UseHttps");
            string protocol = useHttps ? "https" : "http";

            // Builds the list of handler URLs
            var handlerList = rankedInstances
                .Select(ah => new ApplicationHandlerInfo
                {
                    InstanceId = ah.ProcessStat?.InstanceId ?? "unknown",
                    Url = $"{protocol}://{ah.SenderIp}:{ah.Port?.GeneralPort ?? 0}",
                    Ranking = ah.ProcessStat?.PrivateMemoryMB ?? 0
                }).ToList();

            // Enable buffering so body can be read and reused
            context.Request.EnableBuffering();

            // Atualiza lista interna para o FailoverManager
            _failoverManager.UpdateHandlers(handlerList);

            try
            {
                Logger.LogInfo("HANDLERS DISPONÍVEIS:");
                foreach (var handler in handlerList)
                {
                    Logger.LogInfo($" - {handler.InstanceId} | {handler.Url} | TemporarilyUnavailable: {handler.TemporarilyUnavailable}");
                }

                // Usa o FailoverManager para decidir o melhor AH
                var bestHandler = _failoverManager.GetBestHandler();

                if (bestHandler != null)
                {
                    Logger.LogInfo($"Selecionado: {bestHandler.InstanceId} -> {bestHandler.Url}");
                }
                else
                {
                    Logger.LogWarning("Nenhum handler selecionado!");
                }

                if (bestHandler == null)
                    throw new Exception("Nenhum handler disponível no momento.");

                var targetUrl = $"{bestHandler.Url}{context.Request.Path}{context.Request.QueryString}";

                Logger.LogInfo($"Redirecting to Application Handler: {targetUrl}");

                // Garante que o corpo da requisição está posicionado corretamente
                context.Request.Body.Position = 0;

                // Faz o redirecionamento com corpo preservado
                await ProxyUtils.RedirectWithBodyAsync(context, targetUrl);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error forwarding request with failover: {ex.Message}");
                context.Response.StatusCode = 502;
                await context.Response.WriteAsync("Error forwarding to Application Handler with failover.");
            }
        }
    }
}
