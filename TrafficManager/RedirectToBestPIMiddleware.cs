using System.Net.Http;
using Anakim.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Linq;

namespace Anakim.TrafficManager
{
    // Middleware used by the Traffic Manager to redirect requests to the best available Proxy Instance
    public class RedirectToBestPIMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly InstanceRankingManager _rankingManager;
        private readonly FailoverManager _failoverManager;
        private readonly IConfiguration _configuration;

        // Constructor receives dependencies via Dependency Injection
        public RedirectToBestPIMiddleware(
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

        // Middleware logic for handling and redirecting the request
        public async Task InvokeAsync(HttpContext context)
        {
            Logger.LogInfo($"Request received on Host: {context.Request.Host.Host}");

            // Gets the list of ranked Proxy Instances from memory
            var rankedInstances = _rankingManager.GetRankedInstances();

            if (!rankedInstances.Any())
            {
                context.Response.StatusCode = 503; // Service Unavailable
                await context.Response.WriteAsync("No Proxy Instance available.");
                return;
            }

            // Reads the HTTPS usage setting from configuration
            bool useHttps = _configuration.GetValue<bool>("UseHttps");
            var protocol = useHttps ? "https" : "http";

            // Converts statistics into failover handler list
            var handlerList = rankedInstances.Select(pi => new ApplicationHandlerInfo
            {
                InstanceId = pi.ProcessStat?.InstanceId ?? "unknow",
                Url = $"{protocol}://{pi.SenderIp}:{pi.Port?.GeneralPort ?? 0}",
                Ranking = pi.ProcessStat?.PrivateMemoryMB ?? 0
            }).ToList();

            // Updates internal handler list used by FailoverManager
            _failoverManager.UpdateHandlers(handlerList);

            // Gets the best Proxy Instance from the ranking
            var bestInstance = rankedInstances.First();
            if (bestInstance == null || bestInstance.Port?.GeneralPort == null)
            {
                Logger.LogInfo("bestInstance or its bestInstance.Ports is null.");
                return;
            }

            // Constructs the target URL by preserving the path and query string
            var targetUrl = $"{protocol}://{bestInstance.SenderIp}:{bestInstance.Port?.GeneralPort}{context.Request.Path}{context.Request.QueryString}";

            Logger.LogInfo($"Redirecting request to: {targetUrl}");

            // Performs an HTTP redirect to the selected Proxy Instance
            context.Response.StatusCode = StatusCodes.Status302Found; // Use 307 if you want to preserve the method (e.g., for POST)
            context.Response.Headers["Location"] = targetUrl;
        }
    }
}
