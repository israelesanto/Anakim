using System.Collections.Concurrent;
using AnakimOrchestrator.Infrastructure.RequestProtection.Contracts;
using AnakimOrchestrator.Infrastructure.RequestProtection.Enums;
using AnakimOrchestrator.Infrastructure.RequestProtection.Models;

namespace AnakimOrchestrator.Infrastructure.RequestProtection.Stores
{
    public class ProtectedRequestStore : IProtectedRequestStore
    {
        private readonly ConcurrentDictionary<string, ProtectedRequest> _requests = new();

        public bool TryAdd(ProtectedRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId))
            {
                return false;
            }

            return _requests.TryAdd(request.RequestId, request);
        }

        public bool TryUpdate(ProtectedRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.RequestId))
            {
                return false;
            }

            if (!_requests.TryGetValue(request.RequestId, out var currentRequest))
            {
                return false;
            }

            return _requests.TryUpdate(request.RequestId, request, currentRequest);
        }

        public bool TryGet(string requestId, out ProtectedRequest? request)
        {
            var ok = _requests.TryGetValue(requestId, out var found);
            request = found;
            return ok;
        }

        public IReadOnlyCollection<ProtectedRequest> GetAll()
        {
            return _requests.Values
                .OrderBy(x => x.CreatedAtUtc)
                .ToList()
                .AsReadOnly();
        }

        public IReadOnlyCollection<ProtectedRequest> GetByStatus(ProtectedRequestStatus status)
        {
            return _requests.Values
                .Where(x => x.Status == status)
                .OrderBy(x => x.CreatedAtUtc)
                .ToList()
                .AsReadOnly();
        }

        public IReadOnlyCollection<ProtectedRequest> GetPendingRequests()
        {
            return _requests.Values
                .Where(x =>
                    x.Status != ProtectedRequestStatus.Completed &&
                    x.Status != ProtectedRequestStatus.Expired &&
                    x.Status != ProtectedRequestStatus.DeadLettered)
                .OrderBy(x => x.CreatedAtUtc)
                .ToList()
                .AsReadOnly();
        }

        public bool TryRemove(string requestId)
        {
            return _requests.TryRemove(requestId, out _);
        }
    }
}