using AnakimOrchestrator.Infrastructure.RequestProtection.Enums;
using AnakimOrchestrator.Infrastructure.RequestProtection.Models;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Contracts
{
    public interface IProtectedRequestStore
    {
        bool TryAdd(ProtectedRequest request);

        bool TryUpdate(ProtectedRequest request);

        bool TryGet(string requestId, out ProtectedRequest? request);

        IReadOnlyCollection<ProtectedRequest> GetAll();

        IReadOnlyCollection<ProtectedRequest> GetByStatus(ProtectedRequestStatus status);

        IReadOnlyCollection<ProtectedRequest> GetPendingRequests();

        bool TryRemove(string requestId);
    }
}