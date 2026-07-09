using CodexQueue.Api.Domain;

namespace CodexQueue.Api.Services;

public static class QueuePriority
{
    public static int CompareForDisplay(CodexRequest left, CodexRequest right)
    {
        return left.QueueOrder.CompareTo(right.QueueOrder) != 0
            ? left.QueueOrder.CompareTo(right.QueueOrder)
            : left.CreatedAt.CompareTo(right.CreatedAt) != 0
                ? left.CreatedAt.CompareTo(right.CreatedAt)
                : left.Id.CompareTo(right.Id);
    }

    public static void ReorderQueuedAfterActive(IEnumerable<CodexRequest> projectRequests, IReadOnlyList<Guid> queuedRequestIds)
    {
        var requests = projectRequests
            .Where(IsVisiblePriorityRequest)
            .ToArray();
        var queuedById = requests
            .Where(x => x.Status == QueueStatus.Queued)
            .ToDictionary(x => x.Id);
        var requestedSet = new HashSet<Guid>();
        var requestedQueued = new List<CodexRequest>();
        foreach (var requestId in queuedRequestIds)
        {
            if (queuedById.TryGetValue(requestId, out var request) && requestedSet.Add(requestId))
            {
                requestedQueued.Add(request);
            }
        }

        var active = requests
            .Where(x => x.Status is QueueStatus.Running or QueueStatus.CancelRequested)
            .OrderBy(x => x.QueueOrder)
            .ThenBy(x => x.StartedAt ?? x.CreatedAt)
            .ThenBy(x => x.Id);
        var remainingQueued = requests
            .Where(x => x.Status == QueueStatus.Queued && !requestedSet.Contains(x.Id))
            .OrderBy(x => x.QueueOrder)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id);

        var orderedRequests = active.Concat(requestedQueued).Concat(remainingQueued).ToArray();
        var orderSlots = orderedRequests
            .Select(x => x.QueueOrder)
            .Order()
            .ToArray();

        for (var index = 0; index < orderedRequests.Length; index++)
        {
            orderedRequests[index].QueueOrder = orderSlots[index];
        }
    }

    public static void MoveQueuedRequestAfterActive(IEnumerable<CodexRequest> projectRequests, CodexRequest movedRequest)
    {
        ReorderQueuedAfterActive(projectRequests, new[] { movedRequest.Id });
    }

    private static bool IsVisiblePriorityRequest(CodexRequest request) =>
        request.DeletedAt is null &&
        request.ArchivedAt is null &&
        request.Status is QueueStatus.Queued or QueueStatus.Running or QueueStatus.CancelRequested;

}
