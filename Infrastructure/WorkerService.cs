using Microsoft.Extensions.Hosting;
using Anakim.ProxyInstance;
using Anakim.TrafficManager;

namespace Anakim.Infrastructure
{
    // Background service that delegates execution to one of the core services (TM, PI, AH)
    public class WorkerService : BackgroundService
    {
        private readonly IHostedService _service;

        // Constructor receives the actual service implementation via dependency injection
        public WorkerService(IHostedService service)
        {
            // Validates if the provided service is supported
            if (service is not TrafficManagerService &&
                service is not ProxyInstanceService &&
                service is not ApplicationHandlerService)
            {
                throw new ArgumentException("Unsupported service type provided.", nameof(service));
            }

            _service = service;
        }

        // This method runs when the host starts the service
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("WorkerService started.");

            // Delegates execution based on actual service type
            switch (_service)
            {
                case TrafficManagerService tmService:
                    Logger.LogInfo("Running as Traffic Manager Service.");
                    await tmService.StartAsync(stoppingToken);
                    break;

                case ProxyInstanceService piService:
                    Logger.LogInfo("Running as Proxy Instance Service.");
                    await piService.ConnectToTrafficManager(stoppingToken);
                    break;

                case ApplicationHandlerService ahService:
                    Logger.LogInfo("Running as Application Handler Service.");
                    await ahService.ConnectToProxyInstance(stoppingToken);
                    break;

                default:
                    Logger.LogError("Unsupported service type provided.");
                    break;
            }
        }
    }
}
