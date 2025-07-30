using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using AnakimOrchestrator.Infrastructure;
using System.Linq;
using System.Threading.Tasks;

namespace AnakimOrchestrator.ProxyInstance
{
    public class RedirectToBestAHMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;
        private readonly IConfiguration _configuration;

        public RedirectToBestAHMiddleware(
            RequestDelegate next,
            InstanceRankingManager rankingManager,
            FailoverManager failoverManager,
            IConfiguration configuration,
            DockerSettings dockerSettings) // injetado apenas por compatibilidade
        {
            _next = next;
            _rankingManager = rankingManager;
            _failoverManager = failoverManager;
            _configuration = configuration;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("No Application Handler available.");
                return;
            }

            bool useHttps = _configuration.GetValue<bool>("UseHttps");
            string protocol = useHttps ? "https" : "http";

            var handlerList = rankedInstances
                .Select(ah => new ApplicationHandlerInfo
                {
                    InstanceId = ah.ProcessStat?.InstanceId ?? "unknown",
                    InstanceName = ah.ProcessStat?.InstanceName ?? string.Empty,
                    Url = $"{protocol}://{ProxyUtils.GetRedirectHost(_configuration, ah)}:{ah.Port?.GeneralPort ?? 0}",
                    Ranking = ah.ProcessStat?.PrivateMemoryMB ?? 0
                }).ToList();

            context.Request.EnableBuffering();
            _failoverManager.UpdateHandlers(handlerList);

            try
            {
                Logger.LogInfo("HANDLERS DISPONÍVEIS:");
                foreach (var handler in handlerList)
                {
                    Logger.LogInfo($" - {handler.InstanceId} | {handler.Url} | TemporarilyUnavailable: {handler.TemporarilyUnavailable}");
                }

                var bestHandler = _failoverManager.GetBestHandler();

                if (bestHandler != null)
                {
                    Logger.LogInfo($"Selecionado: {bestHandler.InstanceId} -> {bestHandler.Url}");
                }
                else
                {
                    Logger.LogWarning("Nenhum handler selecionado!");
                    throw new Exception("Nenhum handler disponível no momento.");
                }

                var targetUrl = $"{bestHandler.Url}{context.Request.Path}{context.Request.QueryString}";
                Logger.LogInfo($"Redirecting to Application Handler: {targetUrl}");

                context.Request.Body.Position = 0;
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
