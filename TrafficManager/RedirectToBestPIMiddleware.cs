using System.Net.Http;
using AnakimOrchestrator.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace AnakimOrchestrator.TrafficManager
{
    // Middleware used by the Traffic Manager to redirect requests to the best available Proxy Instance
    public class RedirectToBestPIMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;
        private readonly DockerSettings _dockerSettings;
        private readonly IConfiguration _configuration;

        public RedirectToBestPIMiddleware(
            RequestDelegate next,
            InstanceRankingManager rankingManager,
            FailoverManager failoverManager,
            IConfiguration configuration,
            DockerSettings dockerSettings) // mantido por compatibilidade, mas não usado mais
        {
            _next = next;
            _rankingManager = rankingManager;
            _failoverManager = failoverManager;
            _configuration = configuration;
            _dockerSettings = dockerSettings;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync("No Proxy Instance available.");
                return;
            }

            bool useHttps = _configuration.GetValue<bool>("UseHttps");
            var protocol = useHttps ? "https" : "http";

            var handlerList = rankedInstances.Select(pi => new ApplicationHandlerInfo
            {
                InstanceId = pi.ProcessStat?.InstanceId ?? "unknown",
                Url = $"{protocol}://{pi.SenderIp}:{pi.Port?.GeneralPort ?? 0}",
                Ranking = pi.ProcessStat?.PrivateMemoryMB ?? 0
            }).ToList();

            _failoverManager.UpdateHandlers(handlerList);

            var bestInstance = rankedInstances.First();
            if (bestInstance == null || bestInstance.Port?.GeneralPort == null)
            {
                Logger.LogInfo("bestInstance or its Ports is null.");
                return;
            }

            string redirectHost = ProxyUtils.GetRedirectHost(_configuration, bestInstance);

            Logger.LogInfo($"[ROUTE] Redirecionando para host: {redirectHost}");

            var targetUrl = $"{protocol}://{redirectHost}:{bestInstance.Port.GeneralPort}{context.Request.Path}{context.Request.QueryString}";
            Logger.LogInfo($"[ROUTE] URL final: {targetUrl}");

            if (context.Request.Path.StartsWithSegments("/scripts"))
            {
                await ProxyUtils.RedirectWithBodyAsync(context, targetUrl);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers["Location"] = targetUrl;
        }
    }
}
