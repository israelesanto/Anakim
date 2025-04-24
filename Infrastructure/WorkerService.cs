using Microsoft.Extensions.Hosting;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Anakim.Infrastructure
{
    public class WorkerService : BackgroundService
    {
        private readonly IHostedService _service;

        public WorkerService(IHostedService service)
        {
            // Validar se o serviço é suportado
            if (service is not TrafficManagerService &&
                service is not ProxyInstanceService &&
                service is not ApplicationHandlerService)
            {
                throw new ArgumentException("Unsupported service type provided.", nameof(service));
            }

            _service = service;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Logger.LogInfo("WorkerService started.");

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
