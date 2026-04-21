using AnakimOrchestrator.Infrastructure.RequestProtection.Models;
using Microsoft.AspNetCore.Http;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Contracts
{
    public interface IRequestProtectionService
    {
        Task<ProtectedRequest> RegisterAsync(
            HttpContext httpContext,
            string requestBody,
            CancellationToken cancellationToken = default);

        Task MarkAsPersistedAsync(
            string requestId,
            CancellationToken cancellationToken = default);

        Task MarkAsDispatchingAsync(
            string requestId,
            string applicationHandlerInstanceName,
            CancellationToken cancellationToken = default);

        Task MarkAsInFlightAsync(
            string requestId,
            CancellationToken cancellationToken = default);

        Task MarkAsCompletedAsync(
            string requestId,
            int responseStatusCode,
            string? responseBody,
            CancellationToken cancellationToken = default);

        Task MarkAsFailedAsync(
            string requestId,
            string errorMessage,
            CancellationToken cancellationToken = default);

        Task MarkAsUnknownAsync(
            string requestId,
            string errorMessage,
            CancellationToken cancellationToken = default);
    }
}